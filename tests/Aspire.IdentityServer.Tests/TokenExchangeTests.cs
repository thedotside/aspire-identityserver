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
using Microsoft.Playwright;
using Xunit;

namespace Aspire.IdentityServer.Tests;

[Collection("app")]
public sealed class TokenExchangeTests(AppFixture fixture)
{
    [Theory]
    [InlineData("worker")]
    [InlineData("device-secret")]
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
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(clientId, payload.GetProperty("client").GetString());
        Assert.Equal("idp-a:alice", payload.GetProperty("subject").GetString());
        Assert.Null(request.Headers.Authorization);
        using var plain = factory.CreateClient("plain");
        using var unauthenticated = await plain.GetAsync("/orders/a");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        // The same configured client must fail closed when no user token is supplied.
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.GetAsync("/orders/a"));
        using var alreadyAuthenticated = new HttpRequestMessage(HttpMethod.Get, "/orders/a");
        alreadyAuthenticated.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "existing"
        );
        alreadyAuthenticated.Options.Set(TokenExchange.SubjectTokenKey, subject);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orders.SendAsync(alreadyAuthenticated)
        );
        using var insecure = new HttpRequestMessage(HttpMethod.Get, "http://localhost/orders/a");
        insecure.Options.Set(TokenExchange.SubjectTokenKey, subject);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orders.SendAsync(insecure));
        var foreignSubject = (await UserTokens("b")).GetProperty("access_token").GetString()!;
        using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, "/orders/a");
        foreignRequest.Options.Set(TokenExchange.SubjectTokenKey, foreignSubject);
        var rejected = await Assert.ThrowsAsync<HttpRequestException>(() =>
            orders.SendAsync(foreignRequest)
        );
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(foreignRequest.Headers.Authorization);
    }

    [Theory]
    [InlineData("worker", true)]
    [InlineData("device-secret", false)]
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
        Assert.Equal(
            TokenExchange.AccessTokenType,
            payload.GetProperty("issued_token_type").GetString()
        );
        Assert.Equal("Bearer", payload.GetProperty("token_type").GetString());
        Assert.Equal("orders.read", payload.GetProperty("scope").GetString());
        Assert.False(payload.TryGetProperty("refresh_token", out _));
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        Assert.Equal(source.Subject, jwt.Subject);
        Assert.Equal("idp-a:alice", jwt.Subject);
        Assert.Equal(client.Authority, jwt.Issuer);
        Assert.Equal(["orders-api-a"], jwt.Audiences.ToArray());
        Assert.Equal(clientId, jwt.GetClaim("client_id").Value);
        Assert.Equal(source.GetClaim("auth_time").Value, jwt.GetClaim("auth_time").Value);
        Assert.True(jwt.ValidTo <= source.ValidTo);
        using var actor = JsonDocument.Parse(jwt.GetClaim("act").Value);
        Assert.Equal(clientId, actor.RootElement.GetProperty("sub").GetString());
        if (includeName)
            Assert.Equal("Alice A", jwt.GetClaim("name").Value);
        else
            Assert.False(jwt.TryGetClaim("name", out _));
        Assert.False(jwt.TryGetClaim("role", out _));
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await api.GetAsync("/orders/a");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var foreign = await api.GetAsync("/orders/b");
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
    }

    [Theory]
    [InlineData("missing-subject", "invalid_request")]
    [InlineData("id-token-type", "invalid_request")]
    [InlineData("issued-id-token", "invalid_request")]
    [InlineData("actor-token", "invalid_request")]
    [InlineData("resource", "invalid_target")]
    [InlineData("missing-audience", "invalid_request")]
    [InlineData("wrong-audience", "invalid_grant")]
    [InlineData("malformed", "invalid_grant")]
    [InlineData("tampered", "invalid_grant")]
    [InlineData("id-token", "invalid_grant")]
    [InlineData("scope-escalation", "invalid_scope")]
    [InlineData("wrong-secret", "invalid_client")]
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

    [Theory]
    [InlineData("foreign-provider")]
    [InlineData("untrusted-client")]
    [InlineData("missing-source-audience")]
    [InlineData("machine-subject")]
    [InlineData("exchanged-subject")]
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

    [Fact]
    public async Task Clients_without_opt_in_cannot_exchange_tokens()
    {
        var source = await UserTokens("b");
        var client = await fixture.Client("idp-b", "worker");
        var fields = ExchangeFields(client, source.GetProperty("access_token").GetString()!);
        fields["audience"] = "orders-api-b";
        await AssertRejected(fields, "unsupported_grant_type", "b");
    }

    [Fact]
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
        Assert.NotEqual(
            new JsonWebToken(first["client_assertion"]).Id,
            new JsonWebToken(second["client_assertion"]).Id,
            StringComparer.Ordinal
        );
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
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            error,
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error")
                .GetString()
        );
    }

    private async Task<JsonElement> UserTokens(
        string provider = "a",
        string clientId = "web",
        string? scope = null
    )
    {
        var client = await fixture.Client($"idp-{provider}", clientId);
        await using var context = await fixture.Browser.NewContextAsync(
            new() { IgnoreHTTPSErrors = true }
        );
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
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
