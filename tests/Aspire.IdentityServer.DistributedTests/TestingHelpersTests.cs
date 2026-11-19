using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Aspire.IdentityServer.Tests;

/// <summary>The helpers of the Testing package against the real sample AppHost.</summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class TestingHelpersTests(AppFixture fixture)
{
    [Test]
    [Arguments("a", "web", "Alice A")]
    [Arguments("b", "web", "Alice B")]
    [Arguments("a", "public-web", "Alice A")]
    public async Task User_tokens_come_from_the_real_sign_in_without_a_browser(
        string provider,
        string clientId,
        string name
    )
    {
        var tokens = await fixture.App.GetUserTokensAsync(
            $"idp-{provider}",
            clientId,
            "alice",
            "openid profile orders.read"
        );
        var access = new JsonWebToken(tokens.AccessToken);
        await Assert.That(access.Subject).IsEqualTo($"idp-{provider}:alice");
        await Assert.That(access.GetClaim("client_id").Value).IsEqualTo(clientId);
        await Assert.That(new JsonWebToken(tokens.IdToken!).GetClaim("name").Value).IsEqualTo(name);
        await Assert.That(tokens.RefreshToken).IsNull();
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.AccessToken
        );
        var orders = await api.GetFromJsonAsync<JsonElement>($"/orders/{provider}");
        await Assert
            .That(orders.GetProperty("subject").GetString())
            .IsEqualTo($"idp-{provider}:alice");
    }

    [Test]
    public async Task Registered_scopes_are_requested_by_default()
    {
        var tokens = await fixture.App.GetUserTokensAsync("idp-a", "web", "bob");
        await Assert.That(new JsonWebToken(tokens.AccessToken).Subject).IsEqualTo("idp-a:bob");
        // The web client registers offline_access: the provider issues a refresh token.
        await Assert.That(tokens.RefreshToken).IsNotNull();
        await Assert.That(tokens.Scope!.Split(' ')).Contains("orders.read");
    }

    [Test]
    [Arguments("idp-a")]
    [Arguments("idp-b")]
    public async Task Client_credentials_tokens_are_accepted_by_the_api(string provider)
    {
        var tokens = await fixture.App.GetClientCredentialsTokensAsync(provider, "worker");
        await Assert.That(tokens.Scope).IsEqualTo("orders.read");
        await Assert.That(tokens.RefreshToken).IsNull();
        await Assert.That(tokens.ExpiresIn).IsEqualTo(300);
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokens.AccessToken
        );
        var orders = await api.GetFromJsonAsync<JsonElement>(
            $"/orders/{provider["idp-".Length..]}"
        );
        await Assert.That(orders.GetProperty("client").GetString()).IsEqualTo("worker");
    }

    [Test]
    public async Task Client_options_carry_the_resolved_credentials()
    {
        var options = await fixture.App.GetIdentityClientOptionsAsync("idp-b", "worker");
        await Assert
            .That(options.Authority)
            .IsEqualTo(fixture.App.GetEndpoint("idp-b", "https").ToString().TrimEnd('/'));
        await Assert.That(options.AuthenticationMethod).IsEqualTo("client_secret_post");
        await Assert.That(string.IsNullOrWhiteSpace(options.ClientSecret)).IsFalse();
        await Assert.That(options.Scopes).IsEqualTo("orders.read");
    }

    [Test]
    public async Task Unknown_names_and_other_grant_types_are_refused()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetIdentityClientOptionsAsync("missing", "web")
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetIdentityClientOptionsAsync("idp-a", "missing")
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetUserTokensAsync("idp-a", "web", "nobody")
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetUserTokensAsync("idp-a", "worker", "alice")
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetClientCredentialsTokensAsync("idp-a", "web")
        );
    }
}
