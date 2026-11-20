using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Tests;

[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProtocolTests(AppFixture fixture)
{
    [Test]
    [Arguments("imported-jwk")]
    [Arguments("parameter-jwk")]
    [Arguments("legacy-pem")]
    public async Task Imported_jwk_secret_parameter_and_legacy_pem_authenticate_against_the_real_provider(
        string clientId
    )
    {
        var client = await fixture.Client("idp-a", clientId);
        using var response = await TokenRequest("a", Fields(client));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(payload.GetProperty("scope").GetString()).IsEqualTo("orders.read");
        await Assert
            .That(string.IsNullOrWhiteSpace(payload.GetProperty("access_token").GetString()))
            .IsFalse();
    }

    [Test]
    public async Task A_deferred_private_key_must_match_the_public_key_registered_at_the_real_provider()
    {
        var client = await fixture.Client("idp-a", "mismatched-jwk");
        using var response = await TokenRequest("a", Fields(client));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await Error(response)).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task Providers_have_distinct_issuers_and_signing_keys()
    {
        TestContext.Current!.Output.WriteLine(
            $"AppHost to four healthy resources: {fixture.StartupTime.TotalSeconds:F2}s (no Keycloak comparison)."
        );
        using var a = fixture.App.CreateHttpClient("idp-a", "https");
        using var b = fixture.App.CreateHttpClient("idp-b", "https");
        var metadataA = await a.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        var metadataB = await b.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        await Assert
            .That(metadataB.GetProperty("issuer").GetString())
            .IsNotEqualTo(metadataA.GetProperty("issuer").GetString());
        var keysA = await a.GetFromJsonAsync<JsonElement>(
            metadataA.GetProperty("jwks_uri").GetString()
        );
        var keysB = await b.GetFromJsonAsync<JsonElement>(
            metadataB.GetProperty("jwks_uri").GetString()
        );
        await Assert
            .That(keysB.GetProperty("keys")[0].GetProperty("kid").GetString())
            .IsNotEqualTo(keysA.GetProperty("keys")[0].GetProperty("kid").GetString());
        await Assert.That(keysA.GetProperty("keys")[0].TryGetProperty("d", out _)).IsFalse();
    }

    [Test]
    [Arguments("a")]
    [Arguments("b")]
    public async Task Machine_clients_receive_scoped_tokens_accepted_only_by_their_api(
        string provider
    )
    {
        var client = await fixture.Client($"idp-{provider}", "worker");
        using var response = await TokenRequest(provider, Fields(client));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        await Assert.That(jwt.Audiences).Contains($"orders-api-{provider}");
        await Assert.That(jwt.Issuer).IsEqualTo(client.Authority);
        await Assert.That(ScopeClaim(jwt)).IsEqualTo("orders.read");
        await Assert.That(payload.GetProperty("scope").GetString()).IsEqualTo("orders.read");
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await api.GetAsync($"/orders/{provider}");
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var rejected = await api.GetAsync(
            $"/orders/{(string.Equals(provider, "a", StringComparison.Ordinal) ? "b" : "a")}"
        );
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("worker")]
    [Arguments("device-secret")]
    public async Task Client_credentials_handler_calls_the_api_and_reuses_its_token(string clientId)
    {
        var client = await fixture.Client("idp-a", clientId);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Machine:Authority"] = client.Authority,
                    ["Machine:ClientId"] = client.ClientId,
                    ["Machine:AuthenticationMethod"] = client.AuthenticationMethod,
                    ["Machine:ClientSecret"] = client.ClientSecret,
                    ["Machine:PrivateKeyPem"] = client.PrivateKeyPem,
                    ["Machine:PrivateKeyBase64"] = client.PrivateKeyBase64,
                    ["Machine:KeyId"] = client.KeyId,
                    ["Machine:Scopes"] = client.Scopes,
                }
            )
            .Build();
        var apiAddress = fixture.App.GetEndpoint("api", "https");
        var tokenRequests = new CountingHandler();
        var services = new ServiceCollection();
        services
            .AddHttpClient("orders", http => http.BaseAddress = apiAddress)
            .AddClientCredentialsHandler(configuration.GetSection("Machine"), options =>
            {
                options.Scope = "orders.read";
                options.AllowedOrigins.Add(apiAddress);
            });
        services
            .AddHttpClient(TokenExchangeHttpClientExtensions.BackchannelClientName)
            .AddHttpMessageHandler(() => tokenRequests);
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        using var orders = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        for (var call = 0; call < 2; call++)
        {
            var payload = await orders.GetFromJsonAsync<JsonElement>("/orders/a");
            await Assert.That(payload.GetProperty("client").GetString()).IsEqualTo(clientId);
        }
        await Assert.That(tokenRequests.Posts).IsEqualTo(1);
    }

    [Test]
    public async Task The_sample_api_calls_its_partner_with_the_client_credentials_handler()
    {
        using var api = fixture.App.CreateHttpClient("api", "https");
        for (var call = 0; call < 2; call++)
        {
            var orders = await api.GetFromJsonAsync<JsonElement>("/outbound/accounting/orders");
            await Assert.That(orders.GetProperty("client").GetString()).IsEqualTo("worker");
            await Assert.That(orders.GetProperty("subject").GetString()).IsNull();
        }
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        private int posts;
        public int Posts => posts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method == HttpMethod.Post)
                Interlocked.Increment(ref posts);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Test]
    public async Task A_client_access_token_lifetime_is_configurable()
    {
        var client = await fixture.Client("idp-a", "short-lived");
        using var response = await TokenRequest("a", Fields(client));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(payload.GetProperty("expires_in").GetInt32()).IsEqualTo(30);
        var jwt = new JsonWebToken(payload.GetProperty("access_token").GetString()!);
        await Assert.That(jwt.ValidTo - jwt.IssuedAt).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task The_provider_signs_with_the_key_that_the_apphost_generated()
    {
        var parameter = fixture
            .App.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<ParameterResource>()
            .Single(p => string.Equals(p.Name, "idp-a-signing-key", StringComparison.Ordinal));
        await Assert.That(parameter.Secret).IsTrue();
        using var key = JsonDocument.Parse(
            Convert.FromBase64String((await parameter.GetValueAsync(default))!)
        );
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        var keys = await http.GetFromJsonAsync<JsonElement>(
            metadata.GetProperty("jwks_uri").GetString()
        );
        // A provider restarted from the dashboard receives the same key again.
        await Assert
            .That(keys.GetProperty("keys")[0].GetProperty("kid").GetString())
            .IsEqualTo(key.RootElement.GetProperty("kid").GetString());
    }

    [Test]
    [Arguments("a")]
    [Arguments("b")]
    public async Task Projections_configure_multiple_outbound_components_in_one_api(string provider)
    {
        using var api = fixture.App.CreateHttpClient("api", "https");
        var result = await api.GetFromJsonAsync<JsonElement>($"/outbound/{provider}");
        await Assert.That(result.GetProperty("tokenAcquired").GetBoolean()).IsTrue();
    }

    [Test]
    [Arguments("audience", "invalid_client")]
    [Arguments("signature", "invalid_client")]
    [Arguments("scope", "invalid_scope")]
    [Arguments("secret", "invalid_client")]
    public async Task Invalid_client_authentication_and_scopes_are_rejected(
        string change,
        string expected
    )
    {
        var provider = string.Equals(change, "secret", StringComparison.Ordinal) ? "b" : "a";
        var client = await fixture.Client($"idp-{provider}", "worker");
        if (string.Equals(change, "signature", StringComparison.Ordinal))
        {
            using var wrongKey = RSA.Create(2048);
            client.PrivateKeyBase64 = TestKeys.Encode(wrongKey, client.KeyId!);
        }
        var fields = Fields(client);
        if (string.Equals(change, "audience", StringComparison.Ordinal))
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(
                client,
                "https://wrong.example"
            );
        if (string.Equals(change, "scope", StringComparison.Ordinal))
            fields["scope"] = "orders.write";
        if (string.Equals(change, "secret", StringComparison.Ordinal))
            fields["client_secret"] = "incorrect";
        using var response = await TokenRequest(provider, fields);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await Error(response)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("idp-a", "token-endpoint", false)]
    [Arguments("idp-legacy", "token-endpoint", true)]
    [Arguments("idp-legacy", "typed-token-endpoint", false)]
    [Arguments("idp-legacy", "issuer", true)]
    public async Task Token_endpoint_audiences_require_an_explicit_opt_in(
        string provider,
        string profile,
        bool accepted
    )
    {
        var client = await fixture.Client(
            provider,
            string.Equals(provider, "idp-a", StringComparison.Ordinal) ? "worker" : "legacy-worker"
        );
        var tokenEndpoint = $"{client.Authority}/connect/token";
        var fields = Fields(client);
        fields["client_assertion"] = profile switch
        {
            "token-endpoint" => PrivateKeyJwt.CreateTokenEndpointAssertion(client, tokenEndpoint),
            // The explicit type always selects strict validation, whatever the provider option.
            "typed-token-endpoint" => PrivateKeyJwt.CreateAssertion(client, tokenEndpoint),
            _ => PrivateKeyJwt.CreateAssertion(client, client.Authority),
        };
        using var http = fixture.App.CreateHttpClient(provider, "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        if (accepted)
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        else
            await Assert.That(await Error(response)).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task Discovery_advertises_only_RS256_client_assertions()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        var algorithms = metadata
            .GetProperty("token_endpoint_auth_signing_alg_values_supported")
            .EnumerateArray()
            .Select(a => a.GetString()!)
            .ToArray();
        await Assert.That(algorithms).IsEquivalentTo(["RS256"]);
    }

    [Test]
    [Arguments(SecurityAlgorithms.RsaSha256, true)]
    [Arguments(SecurityAlgorithms.RsaSsaPssSha256, false)]
    [Arguments(SecurityAlgorithms.RsaSha512, false)]
    public async Task Only_RS256_client_assertions_are_accepted(string algorithm, bool accepted)
    {
        var client = await fixture.Client("idp-a", "worker");
        var key = new JsonWebKey(
            Encoding.UTF8.GetString(Convert.FromBase64String(client.PrivateKeyBase64!))
        );
        var fields = Fields(client);
        fields["client_assertion"] = new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                TokenType = "client-authentication+jwt",
                Issuer = client.ClientId,
                Audience = client.Authority,
                Expires = DateTime.UtcNow.AddMinutes(1),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sub"] = client.ClientId,
                    ["jti"] = Guid.NewGuid().ToString("N"),
                },
                SigningCredentials = new SigningCredentials(key, algorithm),
            }
        );
        using var response = await TokenRequest("a", fields);
        if (accepted)
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        else
            await Assert.That(await Error(response)).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task A_client_assertion_cannot_be_replayed()
    {
        var fields = Fields(await fixture.Client("idp-a", "worker"));
        using var first = await TokenRequest("a", fields);
        first.EnsureSuccessStatusCode();
        using var replay = await TokenRequest("a", fields);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await Error(replay)).IsEqualTo("invalid_client");
    }

    [Test]
    public async Task A_public_client_cannot_use_client_credentials()
    {
        using var response = await TokenRequest(
            "a",
            new(StringComparer.Ordinal)
            {
                ["client_id"] = "public-web",
                ["grant_type"] = "client_credentials",
                ["scope"] = "orders.read",
            }
        );
        await Assert.That(response.IsSuccessStatusCode).IsFalse();
    }

    internal static Dictionary<string, string> Fields(IdentityClientOptions client)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = client.ClientId,
            ["grant_type"] = "client_credentials",
            ["scope"] = client.Scopes,
        };
        if (string.Equals(client.AuthenticationMethod, "private_key_jwt", StringComparison.Ordinal))
        {
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, client.Authority);
        }
        else
            fields["client_secret"] = client.ClientSecret!;
        return fields;
    }

    /// <summary>The raw scope claim, which must be a JSON string and never an array.</summary>
    internal static string? ScopeClaim(JsonWebToken jwt)
    {
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        var scope = payload.RootElement.GetProperty("scope");
        return scope.ValueKind == JsonValueKind.String ? scope.GetString() : scope.GetRawText();
    }

    internal static async Task<string?> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task<HttpResponseMessage> TokenRequest(
        string provider,
        Dictionary<string, string> fields
    )
    {
        using var http = fixture.App.CreateHttpClient($"idp-{provider}", "https");
        return await http.PostAsync("/connect/token", new FormUrlEncodedContent(fields));
    }
}
