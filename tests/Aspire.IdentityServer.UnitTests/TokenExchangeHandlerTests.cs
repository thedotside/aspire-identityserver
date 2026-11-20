using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aspire.IdentityServer.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Aspire.IdentityServer.Tests;

/// <summary>The exchange handler against in-memory provider and API handlers: no network.</summary>
public sealed class TokenExchangeHandlerTests
{
    [Test]
    [Arguments(Resilience.DefaultsRetry)]
    [Arguments(Resilience.BackchannelRetryAddedLast)]
    [Arguments(Resilience.DefaultsHedging)]
    public async Task Every_attempt_signs_a_new_assertion_and_discovery_is_cached(
        Resilience resilience
    )
    {
        var provider = new FakeProvider { FailedAttempts = 1 };
        var api = new FakeApi();
        await using var services = Build(provider, api, resilience);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        for (var call = 0; call < 2; call++)
        {
            using var response = await orders.SendAsync(UserRequest("https://api.test/orders"));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(provider.Discoveries).IsEqualTo(1);
        // First call: a 503, then a retried or hedged attempt. Second call: one attempt.
        var ids = provider.Assertions.Select(a => new JsonWebToken(a).Id).ToArray();
        await Assert.That(ids.Length).IsEqualTo(3);
        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(3);
        await Assert
            .That(api.Authorizations)
            .IsEquivalentTo(["Bearer exchanged", "Bearer exchanged"]);
    }

    [Test]
    public async Task The_backchannel_budget_bounds_a_silent_provider()
    {
        var provider = new FakeProvider { Delay = TimeSpan.FromSeconds(30) };
        await using var services = Build(provider, new FakeApi(), configure: o =>
            o.BackchannelTimeout = TimeSpan.FromMilliseconds(200)
        );
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            orders.SendAsync(UserRequest("https://api.test/orders"))
        );
    }

    [Test]
    public async Task The_exchanged_token_only_reaches_allowed_origins()
    {
        var provider = new FakeProvider();
        var api = new FakeApi();
        await using var services = Build(provider, api, configure: o =>
            o.AllowedOrigins.Add(new Uri("https://api.test"))
        );
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.SendAsync(UserRequest("https://other.test/orders"))
        );
        await Assert.That(provider.Discoveries).IsEqualTo(0);
        await Assert.That(provider.Assertions).IsEmpty();
        using var allowed = await orders.SendAsync(UserRequest("https://api.test:443/orders"));
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Exchange_errors_surface_the_oauth_error()
    {
        var provider = new FakeProvider { Error = "invalid_target" };
        var api = new FakeApi();
        await using var services = Build(provider, api);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        var error = await Assert.ThrowsExactlyAsync<TokenEndpointException>(() =>
            orders.SendAsync(UserRequest("https://api.test/orders"))
        );
        await Assert.That(error!.Error).IsEqualTo("invalid_target");
        await Assert.That(error.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(api.Authorizations).IsEmpty();
    }

    private static HttpRequestMessage UserRequest(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Options.Set(TokenExchange.SubjectTokenKey, "user-token");
        return request;
    }

    public enum Resilience
    {
        None,

        /// <summary>A retry on every client, registered first, as ServiceDefaults does.</summary>
        DefaultsRetry,

        /// <summary>The standard retry, added to the backchannel after the exchange handler.</summary>
        BackchannelRetryAddedLast,

        /// <summary>The standard hedging handler, which sends cloned requests.</summary>
        DefaultsHedging,
    }

    private static ServiceProvider Build(
        FakeProvider provider,
        FakeApi api,
        Resilience resilience = Resilience.None,
        Action<TokenExchangeHandlerOptions>? configure = null
    )
    {
        using var rsa = RSA.Create(2048);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Device:Authority"] = "https://idp.test",
                    ["Device:ClientId"] = "device",
                    ["Device:AuthenticationMethod"] = "private_key_jwt",
                    ["Device:PrivateKeyBase64"] = TestKeys.Encode(rsa, "device-key"),
                    ["Device:Scopes"] = "orders.read",
                    ["Device:TokenExchangeAudience"] = "orders",
                }
            )
            .Build();
        var services = new ServiceCollection();
        if (resilience == Resilience.DefaultsRetry)
            services.ConfigureHttpClientDefaults(http =>
                http.AddHttpMessageHandler(() => new RetryUnavailable())
            );
        if (resilience == Resilience.DefaultsHedging)
            services.ConfigureHttpClientDefaults(http => http.AddStandardHedgingHandler());
        services
            .AddHttpClient("orders")
            .AddTokenExchangeHandler(configuration.GetSection("Device"), configure)
            .ConfigurePrimaryHttpMessageHandler(() => api);
        var backchannel = services
            .AddHttpClient(TokenExchangeHttpClientExtensions.BackchannelClientName)
            .ConfigurePrimaryHttpMessageHandler(() => provider);
        if (resilience == Resilience.BackchannelRetryAddedLast)
            backchannel.AddStandardResilienceHandler(o =>
            {
                o.Retry.Delay = TimeSpan.Zero;
                o.Retry.UseJitter = false;
            });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class RetryUnavailable : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                return response;
            response.Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeProvider : HttpMessageHandler
    {
        private int discoveries;
        public int FailedAttempts { get; init; }
        public TimeSpan Delay { get; init; }
        public string? Error { get; init; }
        public int Discoveries => discoveries;
        public List<string> Assertions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Delay, cancellationToken);
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref discoveries);
                return Json(
                    new
                    {
                        issuer = "https://idp.test",
                        token_endpoint = "https://idp.test/connect/token",
                    }
                );
            }
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(
                    p => Uri.UnescapeDataString(p[0]),
                    p => Uri.UnescapeDataString(p[1].Replace('+', ' ')),
                    StringComparer.Ordinal
                );
            int attempt;
            lock (Assertions)
            {
                Assertions.Add(form["client_assertion"]);
                attempt = Assertions.Count;
            }
            if (attempt <= FailedAttempts)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Error is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(new { error = Error }),
                };
            return Json(
                new
                {
                    access_token = "exchanged",
                    token_type = "Bearer",
                    issued_token_type = TokenExchange.AccessTokenType,
                }
            );
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            lock (Authorizations)
                Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
