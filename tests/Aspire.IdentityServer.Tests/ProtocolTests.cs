using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.IdentityServer.Tests;

[Collection("app")]
public sealed class ProtocolTests(AppFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Providers_have_distinct_issuers_and_signing_keys()
    {
        output.WriteLine($"AppHost to four healthy resources: {fixture.StartupTime.TotalSeconds:F2}s (no Keycloak comparison).");
        using var a = fixture.App.CreateHttpClient("idp-a", "https");
        using var b = fixture.App.CreateHttpClient("idp-b", "https");
        var metadataA = await a.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        var metadataB = await b.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.NotEqual(metadataA.GetProperty("issuer").GetString(), metadataB.GetProperty("issuer").GetString());
        var keysA = await a.GetFromJsonAsync<JsonElement>(metadataA.GetProperty("jwks_uri").GetString());
        var keysB = await b.GetFromJsonAsync<JsonElement>(metadataB.GetProperty("jwks_uri").GetString());
        Assert.NotEqual(keysA.GetProperty("keys")[0].GetProperty("kid").GetString(), keysB.GetProperty("keys")[0].GetProperty("kid").GetString());
        Assert.False(keysA.GetProperty("keys")[0].TryGetProperty("d", out _));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task Machine_clients_receive_scoped_tokens_accepted_only_by_their_api(string provider)
    {
        var client = await fixture.Client($"idp-{provider}", "worker");
        using var response = await TokenRequest(provider, Fields(client));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = payload.GetProperty("access_token").GetString()!;
        var jwt = new JsonWebToken(token);
        Assert.Contains($"orders-api-{provider}", jwt.Audiences);
        Assert.Equal(client.Authority, jwt.Issuer);
        Assert.Equal("orders.read", payload.GetProperty("scope").GetString());
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var accepted = await api.GetAsync($"/orders/{provider}");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var rejected = await api.GetAsync($"/orders/{(provider == "a" ? "b" : "a")}");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task Projections_configure_multiple_outbound_components_in_one_api(string provider)
    {
        using var api = fixture.App.CreateHttpClient("api", "https");
        var result = await api.GetFromJsonAsync<JsonElement>($"/outbound/{provider}");
        Assert.True(result.GetProperty("tokenAcquired").GetBoolean());
    }

    [Theory]
    [InlineData("audience", "invalid_client")]
    [InlineData("signature", "invalid_client")]
    [InlineData("scope", "invalid_scope")]
    [InlineData("secret", "invalid_client")]
    public async Task Invalid_client_authentication_and_scopes_are_rejected(string change, string expected)
    {
        var provider = change == "secret" ? "b" : "a";
        var client = await fixture.Client($"idp-{provider}", "worker");
        if (change == "signature")
        {
            using var wrongKey = RSA.Create(2048);
            client.PrivateKeyPem = wrongKey.ExportPkcs8PrivateKeyPem();
        }
        var fields = Fields(client);
        if (change == "audience") fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, "https://wrong.example/connect/token");
        if (change == "scope") fields["scope"] = "orders.write";
        if (change == "secret") fields["client_secret"] = "incorrect";
        using var response = await TokenRequest(provider, fields);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_client_assertion_cannot_be_replayed()
    {
        var fields = Fields(await fixture.Client("idp-a", "worker"));
        using var first = await TokenRequest("a", fields);
        first.EnsureSuccessStatusCode();
        using var replay = await TokenRequest("a", fields);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_client", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_public_client_cannot_use_client_credentials()
    {
        using var response = await TokenRequest("a", new()
        {
            ["client_id"] = "public-web",
            ["grant_type"] = "client_credentials",
            ["scope"] = "orders.read"
        });
        Assert.False(response.IsSuccessStatusCode);
    }

    internal static Dictionary<string, string> Fields(IdentityClientOptions client)
    {
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["grant_type"] = "client_credentials",
            ["scope"] = client.Scopes
        };
        if (client.AuthenticationMethod == "private_key_jwt")
        {
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, $"{client.Authority}/connect/token");
        }
        else fields["client_secret"] = client.ClientSecret!;
        return fields;
    }

    private async Task<HttpResponseMessage> TokenRequest(string provider, Dictionary<string, string> fields)
    {
        using var http = fixture.App.CreateHttpClient($"idp-{provider}", "https");
        return await http.PostAsync("/connect/token", new FormUrlEncodedContent(fields));
    }
}
