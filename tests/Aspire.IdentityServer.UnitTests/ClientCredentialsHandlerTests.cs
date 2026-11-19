using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aspire.IdentityServer.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Aspire.IdentityServer.Tests;

/// <summary>The client-credentials handler against in-memory provider and API handlers: no network.</summary>
public sealed class ClientCredentialsHandlerTests
{
    [Test]
    public async Task A_token_is_reused_until_shortly_before_it_expires()
    {
        var time = new ManualTime();
        var provider = new FakeProvider { ExpiresIn = 300 };
        var api = new FakeApi();
        await using var services = Build(provider, api, time: time);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        for (var call = 0; call < 2; call++)
        {
            using var response = await orders.GetAsync("https://api.test/orders");
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        // The default margin renews the token 30 seconds before its expiry.
        time.Advance(TimeSpan.FromSeconds(271));
        using (var renewed = await orders.GetAsync("https://api.test/orders"))
            await Assert.That(renewed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(provider.TokenRequests).IsEqualTo(2);
        await Assert.That(provider.Discoveries).IsEqualTo(1);
        await Assert
            .That(api.Authorizations)
            .IsEquivalentTo(["Bearer token-1", "Bearer token-1", "Bearer token-2"]);
    }

    [Test]
    public async Task Concurrent_requests_share_one_token_request()
    {
        var provider = new FakeProvider { Delay = TimeSpan.FromMilliseconds(100) };
        var api = new FakeApi();
        await using var services = Build(provider, api);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => orders.GetAsync("https://api.test/orders"))
        );
        foreach (var response in responses)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            response.Dispose();
        }
        await Assert.That(provider.TokenRequests).IsEqualTo(1);
    }

    [Test]
    public async Task Every_token_request_attempt_signs_a_new_assertion()
    {
        var provider = new FakeProvider { FailedAttempts = 1 };
        await using var services = Build(provider, new FakeApi(), retryUnavailable: true);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        using var response = await orders.GetAsync("https://api.test/orders");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var ids = provider.Assertions.Select(a => new JsonWebToken(a).Id).ToArray();
        await Assert.That(ids.Length).IsEqualTo(2);
        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(2);
    }

    [Test]
    public async Task A_token_rejected_by_the_api_is_not_reused()
    {
        var provider = new FakeProvider();
        var api = new FakeApi { RejectFirst = true };
        await using var services = Build(provider, api);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        using (var rejected = await orders.GetAsync("https://api.test/orders"))
            await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        using (var accepted = await orders.GetAsync("https://api.test/orders"))
            await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(provider.TokenRequests).IsEqualTo(2);
    }

    [Test]
    public async Task Token_endpoint_errors_surface_the_oauth_error()
    {
        var provider = new FakeProvider
        {
            Error = ("invalid_scope", "The scope orders.write is not allowed."),
        };
        var api = new FakeApi();
        await using var services = Build(provider, api);
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        var error = await Assert.ThrowsExactlyAsync<TokenEndpointException>(() =>
            orders.GetAsync("https://api.test/orders")
        );
        await Assert.That(error!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(error.Error).IsEqualTo("invalid_scope");
        await Assert
            .That(error.ErrorDescription)
            .IsEqualTo("The scope orders.write is not allowed.");
        await Assert.That(error.Message).Contains("invalid_scope");
        await Assert.That(api.Authorizations).IsEmpty();
    }

    [Test]
    public async Task The_token_only_reaches_allowed_https_origins()
    {
        var provider = new FakeProvider();
        var api = new FakeApi();
        await using var services = Build(
            provider,
            api,
            configure: o => o.AllowedOrigins.Add(new Uri("https://api.test"))
        );
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.GetAsync("https://other.test/orders")
        );
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.GetAsync("http://api.test/orders")
        );
        using var authenticated = new HttpRequestMessage(HttpMethod.Get, "https://api.test/orders");
        authenticated.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "existing");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.SendAsync(authenticated)
        );
        await Assert.That(provider.TokenRequests).IsEqualTo(0);
        using var allowed = await orders.GetAsync("https://api.test:443/orders");
        await Assert.That(allowed.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_secret_client_posts_its_secret_and_the_requested_scope()
    {
        var provider = new FakeProvider();
        await using var services = Build(
            provider,
            new FakeApi(),
            secret: "machine-secret",
            configure: o => o.Scope = "orders.write"
        );
        using var orders = services.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        using var response = await orders.GetAsync("https://api.test/orders");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var form = provider.Forms.Single();
        await Assert.That(form["grant_type"]).IsEqualTo("client_credentials");
        await Assert.That(form["client_id"]).IsEqualTo("machine");
        await Assert.That(form["client_secret"]).IsEqualTo("machine-secret");
        await Assert.That(form["scope"]).IsEqualTo("orders.write");
        await Assert.That(form.ContainsKey("client_assertion")).IsFalse();
    }

    [Test]
    public async Task The_backchannel_cannot_install_the_handler()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var backchannel = services.AddHttpClient(
            TokenExchangeHttpClientExtensions.BackchannelClientName
        );
        await Assert
            .That(() =>
                backchannel.AddClientCredentialsHandler(configuration.GetSection("Machine"))
            )
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments("http://localhost/connect/token")]
    [Arguments("/connect/token")]
    public async Task The_request_helper_requires_an_https_endpoint_and_a_confidential_client(
        string endpoint
    )
    {
        var secret = new IdentityClientOptions
        {
            ClientId = "machine",
            AuthenticationMethod = "client_secret_post",
            ClientSecret = "secret",
        };
        await Assert
            .That(() => ClientCredentials.CreateRequest(secret, endpoint))
            .ThrowsExactly<ArgumentException>();
        await Assert
            .That(() =>
                ClientCredentials.CreateRequest(
                    new IdentityClientOptions { ClientId = "public" },
                    "https://localhost/connect/token"
                )
            )
            .ThrowsExactly<ArgumentException>();
    }

    private static ServiceProvider Build(
        FakeProvider provider,
        FakeApi api,
        TimeProvider? time = null,
        bool retryUnavailable = false,
        string? secret = null,
        Action<ClientCredentialsHandlerOptions>? configure = null
    )
    {
        using var rsa = RSA.Create(2048);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Machine:Authority"] = "https://idp.test",
            ["Machine:ClientId"] = "machine",
            ["Machine:Scopes"] = "orders.read",
        };
        if (secret is null)
        {
            values["Machine:AuthenticationMethod"] = "private_key_jwt";
            values["Machine:PrivateKeyBase64"] = TestKeys.Encode(rsa, "machine-key");
        }
        else
        {
            values["Machine:AuthenticationMethod"] = "client_secret_post";
            values["Machine:ClientSecret"] = secret;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        if (time is not null)
            services.AddSingleton(time);
        if (retryUnavailable)
            services.ConfigureHttpClientDefaults(http =>
                http.AddHttpMessageHandler(() => new RetryUnavailable())
            );
        services
            .AddHttpClient("orders")
            .AddClientCredentialsHandler(configuration.GetSection("Machine"), configure)
            .ConfigurePrimaryHttpMessageHandler(() => api);
        services
            .AddHttpClient(TokenExchangeHttpClientExtensions.BackchannelClientName)
            .ConfigurePrimaryHttpMessageHandler(() => provider);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
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
        private int attempts;
        public int FailedAttempts { get; init; }
        public TimeSpan Delay { get; init; }
        public int ExpiresIn { get; init; } = 300;
        public (string Code, string Description)? Error { get; init; }
        public int Discoveries => discoveries;
        public int TokenRequests => Forms.Count(f => !f.ContainsKey("failed"));
        public List<string> Assertions { get; } = [];
        public List<Dictionary<string, string>> Forms { get; } = [];

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
                    HttpStatusCode.OK,
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
            var attempt = Interlocked.Increment(ref attempts);
            lock (Forms)
            {
                if (form.TryGetValue("client_assertion", out var assertion))
                    Assertions.Add(assertion);
                if (attempt <= FailedAttempts)
                    form["failed"] = "true";
                Forms.Add(form);
            }
            if (attempt <= FailedAttempts)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (Error is { } error)
                return Json(
                    HttpStatusCode.BadRequest,
                    new { error = error.Code, error_description = error.Description }
                );
            return Json(
                HttpStatusCode.OK,
                new
                {
                    access_token = $"token-{TokenRequests}",
                    token_type = "Bearer",
                    expires_in = ExpiresIn,
                }
            );
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed class FakeApi : HttpMessageHandler
    {
        private int calls;
        public bool RejectFirst { get; init; }
        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            lock (Authorizations)
                Authorizations.Add(request.Headers.Authorization?.ToString() ?? "");
            var status =
                RejectFirst && Interlocked.Increment(ref calls) == 1
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
