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

    [Test]
    public async Task User_tokens_carry_the_requested_level()
    {
        var tokens = await fixture.App.GetUserTokensAsync(
            "idp-a",
            "web",
            "alice",
            acrValues: "urn:sample:loa:mfa"
        );
        foreach (var token in new[] { tokens.AccessToken, tokens.IdToken! })
        {
            var jwt = new JsonWebToken(token);
            await Assert.That(jwt.GetClaim("acr").Value).IsEqualTo("urn:sample:loa:mfa");
            await Assert.That(Amr(jwt)).IsEquivalentTo(["pwd", "otp", "mfa"]);
        }
    }

    [Test]
    public async Task Another_declared_level_is_issued_when_requested()
    {
        var tokens = await fixture.App.GetUserTokensAsync(
            "idp-a",
            "web",
            "alice",
            acrValues: "urn:sample:loa:high"
        );
        await Assert
            .That(new JsonWebToken(tokens.AccessToken).GetClaim("acr").Value)
            .IsEqualTo("urn:sample:loa:high");
    }

    [Test]
    public async Task Undeclared_acr_values_pass_through_without_a_code()
    {
        var tokens = await fixture.App.GetUserTokensAsync(
            "idp-a",
            "web",
            "alice",
            acrValues: "urn:unknown"
        );
        var access = new JsonWebToken(tokens.AccessToken);
        await Assert.That(access.TryGetClaim("acr", out _)).IsFalse();
        await Assert.That(Amr(access)).IsEquivalentTo(["pwd"]);
    }

    [Test]
    public async Task A_refreshed_token_keeps_acr_and_amr()
    {
        var tokens = await fixture.App.GetUserTokensAsync(
            "idp-a",
            "web",
            "alice",
            acrValues: "urn:sample:loa:mfa"
        );
        var fields = ProtocolTests.Fields(await fixture.Client("idp-a", "web"));
        fields.Remove("scope");
        fields["grant_type"] = "refresh_token";
        fields["refresh_token"] = tokens.RefreshToken!;
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
        await Assert.That(renewed.GetClaim("acr").Value).IsEqualTo("urn:sample:loa:mfa");
        await Assert.That(Amr(renewed)).IsEquivalentTo(["pwd", "otp", "mfa"]);
    }

    [Test]
    public async Task Sign_up_tokens_come_from_the_real_sign_up_page()
    {
        var username = NewUsername();
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-a",
            "web",
            username,
            "Passw0rd!",
            AdaClaims()
        );
        var access = new JsonWebToken(tokens.AccessToken);
        await Assert.That(SignUpTests.IsOpaqueSubject(access.Subject)).IsTrue();
        await Assert.That(Amr(access)).IsEquivalentTo(["pwd"]);
        var id = new JsonWebToken(tokens.IdToken!);
        await Assert.That(id.GetClaim("name").Value).IsEqualTo(username);
        await Assert.That(id.GetClaim("email").Value).IsEqualTo("ada@example.test");
        await Assert.That(id.GetClaim("given_name").Value).IsEqualTo("Ada");
        await Assert.That(id.GetClaim("family_name").Value).IsEqualTo("Lovelace");
        // The web client registers offline_access: the provider issues a refresh token.
        await Assert.That(tokens.RefreshToken).IsNotNull();
    }

    [Test]
    public async Task A_created_account_has_an_opaque_subject_of_fixed_size()
    {
        // OpenID Connect caps a subject at 255 characters: a long username must not reach it.
        var username = NewUsername() + new string('x', 300);
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-a",
            "web",
            username,
            "Passw0rd!",
            AdaClaims()
        );
        var id = new JsonWebToken(tokens.IdToken!);
        await Assert.That(SignUpTests.IsOpaqueSubject(id.Subject)).IsTrue();
        await Assert.That(id.GetClaim("name").Value).IsEqualTo(username);
    }

    [Test]
    public async Task Sign_up_claims_follow_the_requested_scopes()
    {
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-a",
            "web",
            NewUsername(),
            "Passw0rd!",
            AdaClaims(),
            scope: "openid orders.read"
        );
        var id = new JsonWebToken(tokens.IdToken!);
        foreach (var type in new[] { "email", "given_name", "family_name" })
            await Assert.That(id.TryGetClaim(type, out _)).IsFalse();
    }

    [Test]
    public async Task Sign_up_tokens_carry_the_requested_level()
    {
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-a",
            "web",
            NewUsername(),
            "Passw0rd!",
            AdaClaims(),
            acrValues: "urn:sample:loa:mfa"
        );
        foreach (var token in new[] { tokens.AccessToken, tokens.IdToken! })
        {
            var jwt = new JsonWebToken(token);
            await Assert.That(jwt.GetClaim("acr").Value).IsEqualTo("urn:sample:loa:mfa");
            await Assert.That(Amr(jwt)).IsEquivalentTo(["pwd", "otp", "mfa"]);
        }
    }

    [Test]
    public async Task A_signed_up_account_refreshes_its_tokens()
    {
        var username = NewUsername();
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-a",
            "web",
            username,
            "Passw0rd!",
            AdaClaims()
        );
        var fields = ProtocolTests.Fields(await fixture.Client("idp-a", "web"));
        fields.Remove("scope");
        fields["grant_type"] = "refresh_token";
        fields["refresh_token"] = tokens.RefreshToken!;
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
        await Assert.That(renewed.Subject).IsEqualTo(new JsonWebToken(tokens.AccessToken).Subject);
    }

    [Test]
    public async Task A_declared_name_field_names_the_account()
    {
        var tokens = await fixture.App.GetSignUpTokensAsync(
            "idp-legacy",
            "legacy-web",
            NewUsername(),
            "Passw0rd!",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "Ada Lovelace",
                ["email"] = "ada@example.test",
            }
        );
        var names = new JsonWebToken(tokens.IdToken!)
            .Claims.Where(c => string.Equals(c.Type, "name", StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToArray();
        await Assert.That(names).IsEquivalentTo(["Ada Lovelace"]);
    }

    [Test]
    public async Task Sign_up_arguments_are_checked_before_any_request()
    {
        var withoutForm = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-b", "web", NewUsername(), "Passw0rd!")
        );
        await Assert.That(withoutForm!.Message).Contains("WithSignUp");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync(
                "idp-a",
                "worker",
                NewUsername(),
                "Passw0rd!",
                AdaClaims()
            )
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-a", "web", " ", "Passw0rd!", AdaClaims())
        );
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-a", "web", NewUsername(), "", AdaClaims())
        );
        var missing = AdaClaims();
        missing.Remove("family_name");
        var missingError = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-a", "web", NewUsername(), "Passw0rd!", missing)
        );
        await Assert.That(missingError!.Message).Contains("family_name");
        var extra = AdaClaims();
        extra["phone_number"] = "+33 1 23 45 67 89";
        var extraError = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-a", "web", NewUsername(), "Passw0rd!", extra)
        );
        await Assert.That(extraError!.Message).Contains("phone_number");
        // OpenID Connect Prompt Create 1.0 counts a sign-up as done only with an ID token.
        var withoutOpenId = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            fixture.App.GetSignUpTokensAsync(
                "idp-a",
                "web",
                NewUsername(),
                "Passw0rd!",
                AdaClaims(),
                scope: "orders.read"
            )
        );
        await Assert.That(withoutOpenId!.Message).Contains("openid");
    }

    [Test]
    public async Task A_taken_username_fails_with_the_page_message()
    {
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.App.GetSignUpTokensAsync("idp-a", "web", "alice", "Passw0rd!", AdaClaims())
        );
        await Assert.That(error!.Message).Contains("This username is already taken.");
    }

    [Test]
    public async Task Concurrent_sign_ups_create_each_username_once()
    {
        var prefix = NewUsername();
        var distinct = await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(i =>
                    fixture.App.GetSignUpTokensAsync(
                        "idp-a",
                        "web",
                        $"{prefix}-{i}",
                        "Passw0rd!",
                        AdaClaims()
                    )
                )
        );
        await Assert
            .That(
                distinct
                    .Select(t => new JsonWebToken(t.AccessToken).Subject)
                    .Distinct(StringComparer.Ordinal)
                    .Count()
            )
            .IsEqualTo(6);
        var attempts = await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(async _ =>
                {
                    try
                    {
                        await fixture.App.GetSignUpTokensAsync(
                            "idp-a",
                            "web",
                            prefix,
                            "Passw0rd!",
                            AdaClaims()
                        );
                        return true;
                    }
                    catch (InvalidOperationException error)
                        when (error.Message.Contains(
                                "This username is already taken.",
                                StringComparison.Ordinal
                            )
                        )
                    {
                        return false;
                    }
                })
        );
        await Assert.That(attempts.Count(created => created)).IsEqualTo(1);
    }

    private static string NewUsername() => $"ada-{Guid.NewGuid():N}";

    /// <summary>A value for each claim of provider A's sign-up form.</summary>
    private static Dictionary<string, string> AdaClaims() =>
        new(StringComparer.Ordinal)
        {
            ["email"] = "ada@example.test",
            ["given_name"] = "Ada",
            ["family_name"] = "Lovelace",
        };

    private static string[] Amr(JsonWebToken token) =>
        token
            .Claims.Where(c => string.Equals(c.Type, "amr", StringComparison.Ordinal))
            .Select(c => c.Value)
            .ToArray();
}
