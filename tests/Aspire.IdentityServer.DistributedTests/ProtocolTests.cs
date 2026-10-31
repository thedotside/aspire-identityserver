using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Aspire.IdentityServer.Client;
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
                "https://wrong.example/connect/token"
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
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(
                client,
                $"{client.Authority}/connect/token"
            );
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
