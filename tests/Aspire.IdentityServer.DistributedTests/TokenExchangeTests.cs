using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class TokenExchangeTests(AppFixture fixture)
{
    [Test]
    [Arguments("worker")]
    [Arguments("device-secret")]
    public async Task Named_http_client_handler_exchanges_only_for_opted_in_clients(string clientId)
    {
        var subject = (await UserTokens()).GetProperty("access_token").GetString()!;
        var client = await fixture.Client("idp-a", clientId);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Device:Authority"] = client.Authority,
                    ["Device:ClientId"] = client.ClientId,
                    ["Device:AuthenticationMethod"] = client.AuthenticationMethod,
                    ["Device:ClientSecret"] = client.ClientSecret,
                    ["Device:PrivateKeyPem"] = client.PrivateKeyPem,
                    ["Device:PrivateKeyBase64"] = client.PrivateKeyBase64,
                    ["Device:KeyId"] = client.KeyId,
                    ["Device:Scopes"] = client.Scopes,
                    ["Device:TokenExchangeAudience"] = client.TokenExchangeAudience,
                }
            )
            .Build();
        var services = new ServiceCollection();
        var apiAddress = fixture.App.GetEndpoint("api", "https");
        services
            .AddHttpClient("orders", http => http.BaseAddress = apiAddress)
            .AddTokenExchangeHandler(
                configuration.GetSection("Device"),
                options => options.Scope = "orders.read"
            );
        services.AddHttpClient("plain", http => http.BaseAddress = apiAddress);
        using var provider = services.BuildServiceProvider(validateScopes: true);
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var orders = factory.CreateClient("orders");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/a");
        request.Options.Set(TokenExchange.SubjectTokenKey, subject);
        using var response = await orders.SendAsync(request);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(payload.GetProperty("client").GetString()).IsEqualTo(clientId);
        await Assert.That(payload.GetProperty("subject").GetString()).IsEqualTo("idp-a:alice");
        await Assert.That(request.Headers.Authorization).IsNull();
        using var plain = factory.CreateClient("plain");
        using var unauthenticated = await plain.GetAsync("/orders/a");
        await Assert.That(unauthenticated.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        // The same configured client must fail closed when no user token is supplied.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.GetAsync("/orders/a")
        );
        using var alreadyAuthenticated = new HttpRequestMessage(HttpMethod.Get, "/orders/a");
        alreadyAuthenticated.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "existing"
        );
        alreadyAuthenticated.Options.Set(TokenExchange.SubjectTokenKey, subject);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.SendAsync(alreadyAuthenticated)
        );
        using var insecure = new HttpRequestMessage(HttpMethod.Get, "http://localhost/orders/a");
        insecure.Options.Set(TokenExchange.SubjectTokenKey, subject);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            orders.SendAsync(insecure)
        );
        var foreignSubject = (await UserTokens("b")).GetProperty("access_token").GetString()!;
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, "/orders/a");
        foreignRequest.Options.Set(TokenExchange.SubjectTokenKey, foreignSubject);
        var rejected = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            orders.SendAsync(foreignRequest)
        );
        await Assert.That(rejected!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(foreignRequest.Headers.Authorization).IsNull();
    }

    [Test]
    [Arguments("worker", true)]
    [Arguments("device-secret", false)]
    public async Task Same_provider_exchange_preserves_user_and_limits_claims_scopes_and_lifetime(
        string clientId,
        bool includeName
    )
    {
        var original = await UserTokens();
        var source = new JsonWebToken(original.GetProperty("access_token").GetString()!);
        var client = await fixture.Client("idp-a", clientId);
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var request = TokenExchange.CreateRequest(
            client,
            client.Authority + "/connect/token",
            original.GetProperty("access_token").GetString()!,
            scope: "orders.read"
        );
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert
            .That(payload.GetProperty("issued_token_type").GetString())
            .IsEqualTo(TokenExchange.AccessTokenType);
        await Assert.That(payload.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(payload.GetProperty("scope").GetString()).IsEqualTo("orders.read");
        await Assert.That(payload.TryGetProperty("refresh_token", out _)).IsFalse();
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        await Assert.That(jwt.Subject).IsEqualTo(source.Subject);
        await Assert.That(jwt.Subject).IsEqualTo("idp-a:alice");
        await Assert.That(jwt.Issuer).IsEqualTo(client.Authority);
        await Assert.That(jwt.Audiences.ToArray()).IsEquivalentTo(["orders-api-a"]);
        await Assert.That(ProtocolTests.ScopeClaim(jwt)).IsEqualTo("orders.read");
        await Assert.That(jwt.GetClaim("client_id").Value).IsEqualTo(clientId);
        await Assert
            .That(jwt.GetClaim("auth_time").Value)
            .IsEqualTo(source.GetClaim("auth_time").Value);
        await Assert.That(jwt.ValidTo).IsLessThanOrEqualTo(source.ValidTo);
        using var actor = JsonDocument.Parse(jwt.GetClaim("act").Value);
        await Assert.That(actor.RootElement.GetProperty("sub").GetString()).IsEqualTo(clientId);
        if (includeName)
            await Assert.That(jwt.GetClaim("name").Value).IsEqualTo("Alice A");
        else
            await Assert.That(jwt.TryGetClaim("name", out _)).IsFalse();
        await Assert.That(jwt.TryGetClaim("role", out _)).IsFalse();
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await api.GetAsync("/orders/a");
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var foreign = await api.GetAsync("/orders/b");
        await Assert.That(foreign.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("missing-subject", "invalid_request")]
    [Arguments("id-token-type", "invalid_request")]
    [Arguments("issued-id-token", "invalid_request")]
    [Arguments("actor-token", "invalid_request")]
    [Arguments("resource", "invalid_target")]
    [Arguments("missing-audience", "invalid_request")]
    [Arguments("wrong-audience", "invalid_grant")]
    [Arguments("malformed", "invalid_grant")]
    [Arguments("tampered", "invalid_grant")]
    [Arguments("id-token", "invalid_grant")]
    [Arguments("scope-escalation", "invalid_scope")]
    [Arguments("wrong-secret", "invalid_client")]
    public async Task Invalid_exchange_requests_are_rejected(string change, string expected)
    {
        var source = await UserTokens();
        var client = await fixture.Client("idp-a", "device-secret");
        var fields = ExchangeFields(client, source.GetProperty("access_token").GetString()!);
        switch (change)
        {
            case "missing-subject":
                fields.Remove("subject_token");
                break;
            case "id-token-type":
                fields["subject_token_type"] = "urn:ietf:params:oauth:token-type:id_token";
                break;
            case "issued-id-token":
                fields["requested_token_type"] = "urn:ietf:params:oauth:token-type:id_token";
                break;
            case "actor-token":
                fields["actor_token"] = fields["subject_token"];
                break;
            case "resource":
                fields["resource"] = "https://untrusted.example";
                break;
            case "missing-audience":
                fields.Remove("audience");
                break;
            case "wrong-audience":
                fields["audience"] = "orders-api-b";
                break;
            case "malformed":
                fields["subject_token"] = "not-a-jwt";
                break;
            case "tampered":
                var segments = fields["subject_token"].Split('.');
                segments[1] = WebEncoders.Base64UrlEncode(
                    Encoding.UTF8.GetBytes("{\"sub\":\"mallory\"}")
                );
                fields["subject_token"] = string.Join('.', segments);
                break;
            case "id-token":
                fields["subject_token"] = source.GetProperty("id_token").GetString()!;
                break;
            case "scope-escalation":
                fields["scope"] = "orders.write";
                break;
            case "wrong-secret":
                fields["client_secret"] = "incorrect";
                break;
        }
        await AssertRejected(fields, expected);
    }

    [Test]
    [Arguments("foreign-provider")]
    [Arguments("untrusted-client")]
    [Arguments("missing-source-audience")]
    [Arguments("machine-subject")]
    [Arguments("exchanged-subject")]
    public async Task Only_configured_same_provider_authorization_code_subjects_are_accepted(
        string sourceKind
    )
    {
        var client = await fixture.Client("idp-a", "device-secret");
        string source;
        if (string.Equals(sourceKind, "machine-subject", StringComparison.Ordinal))
        {
            using var http = fixture.App.CreateHttpClient("idp-a", "https");
            var fields = ProtocolTests.Fields(await fixture.Client("idp-a", "worker"));
            using var response = await http.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(fields)
            );
            response.EnsureSuccessStatusCode();
            source = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token")
                .GetString()!;
        }
        else
        {
            var tokens = await UserTokens(
                string.Equals(sourceKind, "foreign-provider", StringComparison.Ordinal) ? "b" : "a",
                string.Equals(sourceKind, "untrusted-client", StringComparison.Ordinal)
                    ? "public-web"
                    : "web",
                string.Equals(sourceKind, "missing-source-audience", StringComparison.Ordinal)
                    ? "openid profile"
                    : null
            );
            source = tokens.GetProperty("access_token").GetString()!;
        }
        if (string.Equals(sourceKind, "exchanged-subject", StringComparison.Ordinal))
        {
            using var http = fixture.App.CreateHttpClient("idp-a", "https");
            using var response = await http.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(ExchangeFields(client, source))
            );
            response.EnsureSuccessStatusCode();
            source = (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token")
                .GetString()!;
        }
        await AssertRejected(ExchangeFields(client, source), "invalid_grant");
    }

    [Test]
    public async Task Sender_constrained_subject_tokens_cannot_become_bearer_tokens()
    {
        var source = await UserTokens(dpop: true);
        await Assert.That(source.GetProperty("token_type").GetString()).IsEqualTo("DPoP");
        var token = new JsonWebToken(source.GetProperty("access_token").GetString()!);
        await Assert.That(token.TryGetPayloadValue<JsonElement>("cnf", out _)).IsTrue();
        var client = await fixture.Client("idp-a", "device-secret");
        await AssertRejected(ExchangeFields(client, token.EncodedToken), "invalid_grant");
    }

    [Test]
    public async Task Clients_without_opt_in_cannot_exchange_tokens()
    {
        var source = await UserTokens("b");
        var client = await fixture.Client("idp-b", "worker");
        var fields = ExchangeFields(client, source.GetProperty("access_token").GetString()!);
        fields["audience"] = "orders-api-b";
        await AssertRejected(fields, "unsupported_grant_type", "b");
    }

    [Test]
    public async Task Private_key_helper_uses_a_fresh_assertion_and_the_server_rejects_replay()
    {
        var source = (await UserTokens()).GetProperty("access_token").GetString()!;
        var client = await fixture.Client("idp-a", "worker");
        using var firstRequest = TokenExchange.CreateRequest(
            client,
            client.Authority + "/connect/token",
            source,
            "orders-api-a"
        );
        using var secondRequest = TokenExchange.CreateRequest(
            client,
            client.Authority + "/connect/token",
            source,
            "orders-api-a"
        );
        var first = QueryHelpers
            .ParseQuery(await firstRequest.Content!.ReadAsStringAsync())
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        var second = QueryHelpers
            .ParseQuery(await secondRequest.Content!.ReadAsStringAsync())
            .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        await Assert
            .That(new JsonWebToken(second["client_assertion"]).Id)
            .IsNotEqualTo(new JsonWebToken(first["client_assertion"]).Id);
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        foreach (var fields in new[] { first, second })
        {
            using var response = await http.PostAsync(
                "/connect/token",
                new FormUrlEncodedContent(fields)
            );
            response.EnsureSuccessStatusCode();
        }
        await AssertRejected(first, "invalid_client");
    }

    private static Dictionary<string, string> ExchangeFields(
        IdentityClientOptions client,
        string source
    )
    {
        var fields = ProtocolTests.Fields(client);
        fields["grant_type"] = TokenExchange.GrantType;
        fields["subject_token"] = source;
        fields["subject_token_type"] = TokenExchange.AccessTokenType;
        fields["requested_token_type"] = TokenExchange.AccessTokenType;
        fields["audience"] = "orders-api-a";
        fields["scope"] = "orders.read";
        return fields;
    }

    private async Task AssertRejected(
        Dictionary<string, string> fields,
        string error,
        string provider = "a"
    )
    {
        using var http = fixture.App.CreateHttpClient($"idp-{provider}", "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ProtocolTests.Error(response)).IsEqualTo(error);
    }

    private async Task<JsonElement> UserTokens(
        string provider = "a",
        string clientId = "web",
        string? scope = null,
        bool dpop = false
    )
    {
        var client = await fixture.Client($"idp-{provider}", clientId);
        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var callback = string.Equals(clientId, "public-web", StringComparison.Ordinal)
            ? "public"
            : provider;
        var redirect =
            fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/') + $"/signin-{callback}";
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await page.RouteAsync(
            $"**/signin-{callback}?**",
            route =>
                route.FulfillAsync(new() { ContentType = "text/plain", Body = "Code captured" })
        );
        await page.GotoAsync(
            QueryHelpers.AddQueryString(
                client.Authority + "/connect/authorize",
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["client_id"] = clientId,
                    ["response_type"] = "code",
                    ["scope"] = scope ?? client.Scopes,
                    ["redirect_uri"] = redirect,
                    ["code_challenge_method"] = "S256",
                    ["code_challenge"] = WebEncoders.Base64UrlEncode(
                        SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
                    ),
                    ["state"] = Guid.NewGuid().ToString("N"),
                    ["nonce"] = Guid.NewGuid().ToString("N"),
                }
            )
        );
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("alice");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        // Only observe the committed callback URL; its document load state is irrelevant.
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    Regex.Escape(redirect) + "\\?",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1)
                )
            );
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = QueryHelpers.ParseQuery(new Uri(page.Url).Query)["code"].ToString(),
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        };
        if (!string.Equals(client.AuthenticationMethod, "none", StringComparison.Ordinal))
        {
            foreach (
                var pair in ProtocolTests
                    .Fields(client)
                    .Where(p => p.Key.StartsWith("client_", StringComparison.Ordinal))
            )
                fields[pair.Key] = pair.Value;
        }
        using var http = fixture.App.CreateHttpClient($"idp-{provider}", "https");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        using var proofKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (dpop)
            request.Headers.Add("DPoP", DPoPProof(proofKey, client.Authority + "/connect/token"));
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A DPoP proof (RFC 9449) that binds the issued access token to this key.</summary>
    private static string DPoPProof(ECDsa key, string tokenEndpoint)
    {
        var parameters = key.ExportParameters(false);
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                TokenType = "dpop+jwt",
                IssuedAt = DateTime.UtcNow,
                AdditionalHeaderClaims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["jwk"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["kty"] = "EC",
                        ["crv"] = "P-256",
                        ["x"] = Base64UrlEncoder.Encode(parameters.Q.X!),
                        ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y!),
                    },
                },
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["jti"] = Guid.NewGuid().ToString("N"),
                    ["htm"] = "POST",
                    ["htu"] = tokenEndpoint,
                },
                SigningCredentials = new SigningCredentials(
                    new ECDsaSecurityKey(key),
                    SecurityAlgorithms.EcdsaSha256
                ),
            }
        );
    }
}
