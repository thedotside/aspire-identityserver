using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            .AddTokenExchangeHandler(configuration.GetSection("Device"), options =>
                options.Scope = "orders.read"
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
        var rejected = await Assert.ThrowsExactlyAsync<TokenEndpointException>(() =>
            orders.SendAsync(foreignRequest)
        );
        await Assert.That(rejected!.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(rejected.Error).IsEqualTo("invalid_grant");
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
        await Assert.That(Values(jwt, "amr")).IsEquivalentTo(Values(source, "amr"));
        await Assert.That(Values(jwt, "amr")).IsNotEmpty();
        await Assert.That(jwt.GetClaim("idp").Value).IsEqualTo(source.GetClaim("idp").Value);
        await Assert.That(jwt.ValidTo).IsLessThanOrEqualTo(source.ValidTo);
        using var actor = JsonDocument.Parse(jwt.GetClaim("act").Value);
        await Assert.That(actor.RootElement.GetProperty("sub").GetString()).IsEqualTo(clientId);
        if (includeName)
            await Assert.That(jwt.GetClaim("name").Value).IsEqualTo("Alice A");
        else
            await Assert.That(jwt.TryGetClaim("name", out _)).IsFalse();
        await Assert.That(jwt.TryGetClaim("role", out _)).IsFalse();
        // Identity claims in the source token must not widen the exchange policy's allow-list.
        await Assert.That(jwt.TryGetClaim("member_of", out _)).IsFalse();
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
    [Arguments("wrong-audience", "invalid_target")]
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
    [Arguments("openid communities orders.read", true, true)]
    [Arguments("openid orders.read", false, true)]
    [Arguments("openid communities unrelated.read", true, false)]
    [Arguments("openid unrelated.read", false, false)]
    public async Task Requested_identity_scopes_select_claims_in_the_login_access_token(
        string scope,
        bool membership,
        bool ordersAudience
    )
    {
        var tokens = await UserTokens(scope: scope);
        var identity = await ValidateIssuedToken(
            tokens.GetProperty("id_token").GetString()!,
            "web"
        );
        var access = await ValidateIssuedToken(
            tokens.GetProperty("access_token").GetString()!,
            ordersAudience ? "orders-api-a" : "unrelated-api"
        );
        await Assert.That(identity.TryGetClaim("member_of", out _)).IsEqualTo(membership);
        await Assert.That(access.TryGetClaim("member_of", out _)).IsEqualTo(membership);
        await Assert.That(ProtocolTests.ScopeClaim(access)).IsEqualTo(scope);
        if (membership)
            await Assert
                .That(Values(access, "member_of"))
                .IsEquivalentTo(["product.readers", "product.contributors"]);
        foreach (var type in new[] { "email", "team", "given_name", "family_name" })
            await Assert.That(access.TryGetClaim(type, out _)).IsFalse();
        await Assert.That(access.Subject).IsEqualTo(identity.Subject);
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access.EncodedToken
        );
        var userInfo = await http.GetFromJsonAsync<JsonElement>("/connect/userinfo");
        await Assert.That(userInfo.GetProperty("sub").GetString()).IsEqualTo(identity.Subject);
        await Assert.That(userInfo.TryGetProperty("member_of", out _)).IsEqualTo(membership);
        if (membership)
            await Assert
                .That(
                    userInfo
                        .GetProperty("member_of")
                        .EnumerateArray()
                        .Select(v => v.GetString()!)
                        .ToArray()
                )
                .IsEquivalentTo(["product.readers", "product.contributors"]);
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access.EncodedToken
        );
        using var response = await api.GetAsync("/memberships/a");
        await Assert
            .That(response.StatusCode)
            .IsEqualTo(
                !ordersAudience ? HttpStatusCode.Unauthorized
                : membership ? HttpStatusCode.OK
                : HttpStatusCode.Forbidden
            );
    }

    [Test]
    public async Task An_identity_scope_does_not_create_missing_user_memberships()
    {
        var tokens = await UserTokens(scope: "openid communities orders.read", username: "bob");
        var access = await ValidateIssuedToken(
            tokens.GetProperty("access_token").GetString()!,
            "orders-api-a"
        );
        await Assert.That(access.Subject).IsEqualTo("idp-a:bob");
        await Assert.That(access.TryGetClaim("member_of", out _)).IsFalse();
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access.EncodedToken
        );
        using var response = await api.GetAsync("/memberships/a");
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task A_machine_token_has_no_user_membership_and_cannot_request_communities()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(ProtocolTests.Fields(await fixture.Client("idp-a", "worker")))
        );
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var access = await ValidateIssuedToken(
            tokens.GetProperty("access_token").GetString()!,
            "orders-api-a"
        );
        await Assert.That(access.TryGetClaim("member_of", out _)).IsFalse();
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            access.EncodedToken
        );
        using var result = await api.GetAsync("/memberships/a");
        await Assert.That(result.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var fields = ProtocolTests.Fields(await fixture.Client("idp-a", "worker"));
        fields["scope"] = "orders.read communities";
        using var rejected = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ProtocolTests.Error(rejected)).IsEqualTo("invalid_scope");
    }

    [Test]
    public async Task A_communities_login_token_without_api_scopes_can_be_exchanged_for_capability_access()
    {
        // No Xp: the BFF asks for identity scopes only, then explicitly delegates to Capa.
        var tokens = await UserTokens(scope: "openid communities");
        var source = new JsonWebToken(tokens.GetProperty("access_token").GetString()!);
        await Assert.That(source.Audiences.ToArray()).IsEmpty();
        await Assert.That(ProtocolTests.ScopeClaim(source)).IsEqualTo("openid communities");
        await Assert
            .That(Values(source, "member_of"))
            .IsEquivalentTo(["product.readers", "product.contributors"]);
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            source.EncodedToken
        );
        var userInfo = await http.GetFromJsonAsync<JsonElement>("/connect/userinfo");
        await Assert.That(userInfo.GetProperty("sub").GetString()).IsEqualTo(source.Subject);
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            source.EncodedToken
        );
        using var direct = await api.GetAsync("/orders/a");
        await Assert.That(direct.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);

        var fields = ExchangeFields(await fixture.Client("idp-a", "delegate"), source.EncodedToken);
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        response.EnsureSuccessStatusCode();
        var exchanged = await response.Content.ReadFromJsonAsync<JsonElement>();
        var target = await ValidateIssuedToken(
            exchanged.GetProperty("access_token").GetString()!,
            "orders-api-a"
        );
        await Assert.That(target.Subject).IsEqualTo(source.Subject);
        await Assert.That(ProtocolTests.ScopeClaim(target)).IsEqualTo("orders.read");
        await Assert
            .That(Values(target, "member_of"))
            .IsEquivalentTo(["product.readers", "product.contributors"]);
        using var actor = JsonDocument.Parse(target.GetClaim("act").Value);
        await Assert.That(actor.RootElement.GetProperty("sub").GetString()).IsEqualTo("delegate");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            target.EncodedToken
        );
        using var accepted = await api.GetAsync("/orders/a");
        await Assert.That(accepted.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    private async Task<JsonWebToken> ValidateIssuedToken(string token, string audience)
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        var metadata = await http.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        var jwks = await http.GetStringAsync(metadata.GetProperty("jwks_uri").GetString());
        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = metadata.GetProperty("issuer").GetString(),
                ValidAudience = audience,
                IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
                ValidateIssuerSigningKey = true,
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            }
        );
        await Assert.That(validated.IsValid).IsTrue();
        return (JsonWebToken)validated.SecurityToken;
    }

    [Test]
    public async Task Requested_identity_scopes_issue_profile_claims_and_offline_access_a_refresh_token()
    {
        var tokens = await UserTokens();
        var identity = new JsonWebToken(tokens.GetProperty("id_token").GetString()!);
        await Assert.That(identity.GetClaim("email").Value).IsEqualTo("alice@example.test");
        await Assert.That(identity.GetClaim("given_name").Value).IsEqualTo("Alice");
        await Assert.That(identity.GetClaim("family_name").Value).IsEqualTo("Anderson");
        // Several values of one claim type are a JSON array.
        await Assert.That(Values(identity, "team")).IsEquivalentTo(["sales", "support"]);
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsTrue();
    }

    [Test]
    public async Task Profile_claims_and_refresh_tokens_are_only_issued_for_their_scopes()
    {
        var tokens = await UserTokens(scope: "openid orders.read");
        var identity = new JsonWebToken(tokens.GetProperty("id_token").GetString()!);
        foreach (var type in new[] { "email", "given_name", "family_name", "team" })
            await Assert.That(identity.TryGetClaim(type, out _)).IsFalse();
        await Assert.That(tokens.TryGetProperty("refresh_token", out _)).IsFalse();
    }

    [Test]
    public async Task A_refresh_token_renews_a_user_token_that_can_still_be_exchanged()
    {
        var tokens = await UserTokens();
        var original = new JsonWebToken(tokens.GetProperty("access_token").GetString()!);
        var browser = await fixture.Client("idp-a", "web");
        var fields = ProtocolTests.Fields(browser);
        fields.Remove("scope");
        fields["grant_type"] = "refresh_token";
        fields["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!;
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        response.EnsureSuccessStatusCode();
        var renewed = new JsonWebToken(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token")
                .GetString()!
        );
        await Assert.That(renewed.Id).IsNotEqualTo(original.Id);
        await Assert.That(renewed.Subject).IsEqualTo("idp-a:alice");
        await Assert
            .That(Values(renewed, "member_of"))
            .IsEquivalentTo(["product.readers", "product.contributors"]);
        await Assert
            .That(renewed.GetClaim("auth_time").Value)
            .IsEqualTo(original.GetClaim("auth_time").Value);
        // A renewed token keeps the facts of the original sign-in, so it can still be exchanged.
        var exchange = ExchangeFields(
            await fixture.Client("idp-a", "delegate"),
            renewed.EncodedToken
        );
        using var exchanged = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(exchange)
        );
        exchanged.EnsureSuccessStatusCode();
    }

    [Test]
    [Arguments("orders.read", HttpStatusCode.OK)]
    [Arguments("orders.write", HttpStatusCode.Forbidden)]
    public async Task Delegated_exchange_grants_machine_client_scopes_absent_from_the_user_token(
        string scope,
        HttpStatusCode expectedByApi
    )
    {
        // A session token without any API scope, hence without the orders audience.
        var source = new JsonWebToken(
            (await UserTokens(scope: "openid profile email teams"))
                .GetProperty("access_token")
                .GetString()!
        );
        await Assert.That(source.Audiences.ToArray()).DoesNotContain("orders-api-a");
        var fields = ExchangeFields(await fixture.Client("idp-a", "delegate"), source.EncodedToken);
        fields["scope"] = scope;
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        response.EnsureSuccessStatusCode();
        var token = new JsonWebToken(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token")
                .GetString()!
        );
        await Assert.That(token.Subject).IsEqualTo("idp-a:alice");
        await Assert.That(token.Audiences.ToArray()).IsEquivalentTo(["orders-api-a"]);
        await Assert.That(ProtocolTests.ScopeClaim(token)).IsEqualTo(scope);
        await Assert.That(token.GetClaim("email").Value).IsEqualTo("alice@example.test");
        await Assert.That(Values(token, "team")).IsEquivalentTo(["sales", "support"]);
        await Assert.That(token.TryGetClaim("given_name", out _)).IsFalse();
        using var actor = JsonDocument.Parse(token.GetClaim("act").Value);
        await Assert.That(actor.RootElement.GetProperty("sub").GetString()).IsEqualTo("delegate");
        // The API still enforces its own scope: /orders/a requires orders.read.
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token.EncodedToken
        );
        using var result = await api.GetAsync("/orders/a");
        await Assert.That(result.StatusCode).IsEqualTo(expectedByApi);
    }

    [Test]
    [Arguments("missing-scope")]
    [Arguments("scope-of-another-provider")]
    public async Task Delegated_exchange_needs_an_explicit_scope_of_the_machine_client(
        string change
    )
    {
        var source = (await UserTokens(scope: "openid profile"))
            .GetProperty("access_token")
            .GetString()!;
        var fields = ExchangeFields(await fixture.Client("idp-a", "delegate"), source);
        if (string.Equals(change, "missing-scope", StringComparison.Ordinal))
            fields.Remove("scope");
        else
            fields["scope"] = "legacy.read";
        await AssertRejected(fields, "invalid_scope");
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

    [Test]
    public async Task An_exchanged_token_keeps_the_level_of_its_source()
    {
        var source = await fixture.App.GetUserTokensAsync(
            "idp-a",
            "web",
            "alice",
            acrValues: "urn:sample:loa:mfa"
        );
        var issued = await ExchangedToken(
            await fixture.Client("idp-a", "device-secret"),
            source.AccessToken
        );
        await Assert.That(issued.GetClaim("acr").Value).IsEqualTo("urn:sample:loa:mfa");
        await Assert.That(Values(issued, "amr")).Contains("mfa");
    }

    [Test]
    public async Task A_token_exchanged_from_another_provider_keeps_its_level()
    {
        var source = await fixture.App.GetUserTokensAsync(
            "idp-b",
            "web",
            "alice",
            acrValues: "urn:sample:loa:mfa"
        );
        var issued = await ExchangedToken(
            await fixture.Client("idp-a", "partner"),
            source.AccessToken
        );
        await Assert.That(issued.GetClaim("acr").Value).IsEqualTo("urn:sample:loa:mfa");
        await Assert.That(Values(issued, "amr")).Contains("mfa");
    }

    [Test]
    public async Task An_exchanged_token_without_a_source_level_has_no_acr()
    {
        var issued = await ExchangedToken(
            await fixture.Client("idp-a", "partner"),
            await SourceToken()
        );
        await Assert.That(issued.TryGetClaim("acr", out _)).IsFalse();
    }

    private async Task<JsonWebToken> ExchangedToken(IdentityClientOptions client, string subject)
    {
        using var response = await Exchange(client, subject);
        response.EnsureSuccessStatusCode();
        return new JsonWebToken(
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token")
                .GetString()!
        );
    }

    private static string[] Values(JsonWebToken token, string type) =>
        token
            .Claims.Where(c => string.Equals(c.Type, type, StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToArray();

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

    [Test]
    public async Task A_user_token_of_another_provider_is_exchanged_only_through_explicit_trust()
    {
        var sourceToken = (await UserTokens("b")).GetProperty("access_token").GetString()!;
        var partner = await fixture.Client("idp-a", "partner");
        using var response = await Exchange(partner, sourceToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(payload.TryGetProperty("refresh_token", out _)).IsFalse();
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        var source = new JsonWebToken(sourceToken);
        await Assert.That(jwt.Issuer).IsEqualTo(partner.Authority);
        await Assert.That(jwt.Audiences.ToArray()).IsEquivalentTo(["orders-api-a"]);
        await Assert.That(ProtocolTests.ScopeClaim(jwt)).IsEqualTo("orders.read");
        // A stable local identity built from the source provider and subject: the alice of
        // provider B is never the alice of provider A.
        await Assert.That(jwt.Subject).IsEqualTo("idp-b|idp-b:alice");
        await Assert.That(jwt.Subject).IsNotEqualTo("idp-a:alice");
        await Assert.That(jwt.GetClaim("idp").Value).IsEqualTo("idp-b");
        await Assert.That(jwt.GetClaim("name").Value).IsEqualTo("Alice B");
        await Assert.That(Values(jwt, "amr")).IsEquivalentTo(Values(source, "amr"));
        await Assert.That(jwt.ValidTo).IsLessThanOrEqualTo(source.ValidTo);
        using (var actor = JsonDocument.Parse(jwt.GetClaim("act").Value))
            await Assert
                .That(actor.RootElement.GetProperty("sub").GetString())
                .IsEqualTo("partner");
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var orders = await api.GetFromJsonAsync<JsonElement>("/orders/a");
        await Assert.That(orders.GetProperty("subject").GetString()).IsEqualTo("idp-b|idp-b:alice");
        // Provider A's API still refuses the token of provider B itself.
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            sourceToken
        );
        using var refused = await api.GetAsync("/orders/a");
        await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    [Arguments("reverse", null)]
    [Arguments("same-provider", "invalid_grant")]
    [Arguments("machine", "invalid_grant")]
    [Arguments("audience", "invalid_target")]
    [Arguments("scope", "invalid_scope")]
    [Arguments("tampered", "invalid_grant")]
    [Arguments("unknown-key", "invalid_grant")]
    public async Task Cross_provider_exchanges_refuse_what_the_trust_does_not_cover(
        string change,
        string? expected
    )
    {
        var partner = await fixture.Client("idp-a", "partner");
        HttpResponseMessage response;
        switch (change)
        {
            case "reverse":
                // Provider B does not trust provider A in return.
                var tokenA = (await UserTokens("a")).GetProperty("access_token").GetString()!;
                response = await Exchange(
                    await fixture.Client("idp-b", "worker"),
                    tokenA,
                    "orders-api-b"
                );
                break;
            case "same-provider":
                // The partner trusts provider B's web client, not provider A's client of the same name.
                var ownToken = (await UserTokens("a")).GetProperty("access_token").GetString()!;
                response = await Exchange(partner, ownToken);
                break;
            case "machine":
                var worker = await fixture.Client("idp-b", "worker");
                using (var http = fixture.App.CreateHttpClient("idp-b", "https"))
                using (
                    var machine = await http.PostAsync(
                        "/connect/token",
                        new FormUrlEncodedContent(ProtocolTests.Fields(worker))
                    )
                )
                {
                    var machineToken = (await machine.Content.ReadFromJsonAsync<JsonElement>())
                        .GetProperty("access_token")
                        .GetString()!;
                    response = await Exchange(partner, machineToken);
                }
                break;
            case "audience":
                response = await Exchange(partner, await SourceToken(), "unrelated-api");
                break;
            case "scope":
                response = await Exchange(partner, await SourceToken(), scope: "orders.write");
                break;
            case "tampered":
                var parts = (await SourceToken()).Split('.');
                var claims = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
                claims["sub"] = "idp-b:bob";
                response = await Exchange(
                    partner,
                    $"{parts[0]}.{Base64UrlEncoder.Encode(claims.ToJsonString())}.{parts[2]}"
                );
                break;
            default:
                response = await Exchange(partner, ForgedSourceToken());
                break;
        }
        using (response)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            if (expected is not null)
                await Assert.That(await ProtocolTests.Error(response)).IsEqualTo(expected);
        }
    }

    private async Task<string> SourceToken() =>
        (await UserTokens("b")).GetProperty("access_token").GetString()!;

    /// <summary>A well-formed token of provider B's issuer, signed by a key it never published.</summary>
    private string ForgedSourceToken()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                TokenType = "at+jwt",
                Issuer = fixture.App.GetEndpoint("idp-b", "https").ToString().TrimEnd('/'),
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sub"] = "idp-b:alice",
                    ["client_id"] = "web",
                    ["scope"] = "openid profile orders.read",
                    ["auth_time"] = new DateTimeOffset(now).ToUnixTimeSeconds(),
                    ["amr"] = new[] { "pwd" },
                    ["idp"] = "local",
                },
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(rsa) { KeyId = "unknown-key" },
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );
    }

    private async Task<HttpResponseMessage> Exchange(
        IdentityClientOptions client,
        string subjectToken,
        string? audience = null,
        string scope = "orders.read"
    )
    {
        var provider = client.Authority.Contains(
            fixture.App.GetEndpoint("idp-b", "https").Authority,
            StringComparison.Ordinal
        )
            ? "idp-b"
            : "idp-a";
        using var http = fixture.App.CreateHttpClient(provider, "https");
        using var request = TokenExchange.CreateRequest(
            client,
            client.Authority + "/connect/token",
            subjectToken,
            audience ?? "orders-api-a",
            scope
        );
        return await http.SendAsync(request);
    }

    private async Task<JsonElement> UserTokens(
        string provider = "a",
        string clientId = "web",
        string? scope = null,
        bool dpop = false,
        string username = "alice"
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
        await page.RouteAsync($"**/signin-{callback}?**", route =>
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
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync(username);
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
