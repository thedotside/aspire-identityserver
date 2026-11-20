using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class BrowserTests(AppFixture fixture)
{
    [Test]
    [Arguments("a")]
    [Arguments("b")]
    [Arguments("public")]
    public async Task Oidc_login_honors_max_age_with_the_framework_handler(string provider)
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync($"{origin}/login/{provider}");
        var loginQuery = QueryHelpers.ParseQuery(new Uri(page.Url).Query);
        var authorize = new Uri(origin + loginQuery["ReturnUrl"].ToString());
        await Assert
            .That(QueryHelpers.ParseQuery(authorize.Query)["max_age"].ToString())
            .IsEqualTo("300");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        var response = await session.Context.APIRequest.GetAsync($"{origin}/me/{provider}");
        try
        {
            await Assert.That(response.Status).IsEqualTo(200);
        }
        finally
        {
            await response.DisposeAsync();
        }
    }

    [Test]
    [Arguments("private-key", "worker", "Alice A")]
    [Arguments("secret", "device-secret", null)]
    public async Task Named_client_can_use_the_current_browser_user(
        string method,
        string machineClient,
        string? name
    )
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync(origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        var payload = await DelegatedOrders(context, origin, method);
        await Assert.That(payload.GetProperty("subject").GetString()).IsEqualTo("idp-a:alice");
        await Assert.That(payload.GetProperty("client").GetString()).IsEqualTo(machineClient);
        await Assert.That(payload.GetProperty("name").GetString()).IsEqualTo(name);
    }

    [Test]
    public async Task A_pooled_named_client_handler_keeps_concurrent_browser_users_isolated()
    {
        await using var aliceSession = await fixture.NewBrowserSessionAsync();
        var alice = aliceSession.Context;
        await using var bobSession = await fixture.NewBrowserSessionAsync();
        var bob = bobSession.Context;
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        foreach (var (context, username) in new[] { (alice, "alice"), (bob, "bob") })
        {
            var page = await context.NewPageAsync();
            await page.GotoAsync(origin + "/login/a");
            await Login(page, username);
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var results = await Task.WhenAll(
                DelegatedOrders(alice, origin, "private-key"),
                DelegatedOrders(bob, origin, "private-key")
            );
            await Assert
                .That(results[0].GetProperty("subject").GetString())
                .IsEqualTo("idp-a:alice");
            await Assert.That(results[0].GetProperty("name").GetString()).IsEqualTo("Alice A");
            await Assert.That(results[1].GetProperty("subject").GetString()).IsEqualTo("idp-a:bob");
            await Assert.That(results[1].GetProperty("name").GetString()).IsEqualTo("Bob A");
        }
    }

    [Test]
    public async Task Provider_pages_and_cookies_are_hardened()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var login = await http.GetAsync("/Account/Login?ReturnUrl=%2F");
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert
            .That(string.Join(' ', login.Headers.GetValues("Content-Security-Policy")))
            .Contains("frame-ancestors 'none'");
        await Assert
            .That(string.Join(' ', login.Headers.GetValues("Referrer-Policy")))
            .IsEqualTo("no-referrer");
        var antiforgery = login
            .Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("aspire-idp-idp-a.csrf=", StringComparison.Ordinal));
        await Assert.That(antiforgery).Contains("secure", StringComparison.OrdinalIgnoreCase);

        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync(origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        var provider = fixture.App.GetEndpoint("idp-a", "https").ToString();
        var cookies = (await context.CookiesAsync([provider]))
            .Where(c => c.Name.StartsWith("aspire-idp-idp-a", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(cookies.Select(c => c.Name).ToArray()).Contains("aspire-idp-idp-a");
        await Assert
            .That(cookies.Select(c => c.Name).ToArray())
            .Contains("aspire-idp-idp-a.session");
        foreach (var cookie in cookies)
        {
            await Assert.That(cookie.Secure).IsTrue();
            await Assert.That(cookie.SameSite).IsNotEqualTo(SameSiteAttribute.None);
        }
        await Assert
            .That(
                cookies
                    .Single(c =>
                        string.Equals(c.Name, "aspire-idp-idp-a", StringComparison.Ordinal)
                    )
                    .SameSite
            )
            .IsEqualTo(SameSiteAttribute.Lax);
    }

    private static async Task<JsonElement> DelegatedOrders(
        IBrowserContext context,
        string origin,
        string method
    )
    {
        var response = await context.APIRequest.GetAsync(origin + $"/delegated-orders/{method}");
        try
        {
            await Assert.That(response.Status).IsEqualTo(200);
            using var document = JsonDocument.Parse(await response.TextAsync());
            return document.RootElement.Clone();
        }
        finally
        {
            await response.DisposeAsync();
        }
    }

    [Test]
    [Arguments("a", "Alice A")]
    [Arguments("b", "Alice B")]
    [Arguments("public", "Alice A")]
    public async Task Browser_can_sign_in_with_pkce_and_sign_out(string provider, string name)
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync($"{origin}/login/{provider}");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        await Assert.That(await page.Locator($"#user-{provider}").InnerTextAsync()).IsEqualTo(name);
        // The application sends its ID token: like a production provider, the provider signs
        // out without asking and returns to the registered post-logout address.
        await page.GetByRole(AriaRole.Button, new() { Name = $"Sign out {provider}", Exact = true })
            .ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        await Assert
            .That(await page.Locator($"#user-{provider}").InnerTextAsync())
            .IsEqualTo("Signed out");
        await page.GotoAsync($"{origin}/login/{provider}");
        await Assertions.Expect(page.GetByLabel("Username")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Two_provider_sessions_coexist_and_sign_out_independently()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        foreach (var provider in new[] { "a", "b" })
        {
            await page.GotoAsync($"{origin}/login/{provider}");
            await Login(page);
            await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        }
        await Assert.That(await page.Locator("#user-a").InnerTextAsync()).IsEqualTo("Alice A");
        await Assert.That(await page.Locator("#user-b").InnerTextAsync()).IsEqualTo("Alice B");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out a", Exact = true })
            .ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        await Assert.That(await page.Locator("#user-a").InnerTextAsync()).IsEqualTo("Signed out");
        await Assert.That(await page.Locator("#user-b").InnerTextAsync()).IsEqualTo("Alice B");
    }

    [Test]
    public async Task Sign_out_without_an_id_token_hint_asks_for_confirmation()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync(origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(origin + "/");
        // Any page can send an unauthenticated end-session request: the user confirms it.
        var provider = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');
        await page.GotoAsync(provider + "/connect/endsession");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Signed out" }))
            .ToBeVisibleAsync();
        await page.GotoAsync(origin + "/login/a");
        await Assertions.Expect(page.GetByLabel("Username")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_hint_prefills_the_username()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(
            PublicClientAuthorizeUrl(
                "hint-state",
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["login_hint"] = "bob" }
            )
        );
        await Assertions.Expect(page.GetByLabel("Username")).ToHaveValueAsync("bob");
        await Assertions.Expect(page.GetByLabel("Password")).ToHaveValueAsync("");
    }

    [Test]
    public async Task Cancelling_sign_in_returns_access_denied_to_the_client()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var redirect =
            fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/') + "/signin-public";
        await page.RouteAsync(
            "**/signin-public?**",
            route =>
                route.FulfillAsync(new() { ContentType = "text/plain", Body = "Callback captured" })
        );
        await page.GotoAsync(PublicClientAuthorizeUrl("cancel-state"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    Regex.Escape(redirect) + "\\?",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1)
                )
            );
        var query = QueryHelpers.ParseQuery(new Uri(page.Url).Query);
        await Assert.That(query["error"].ToString()).IsEqualTo("access_denied");
        await Assert.That(query["state"].ToString()).IsEqualTo("cancel-state");
        await Assert.That(query.ContainsKey("code")).IsFalse();
    }

    /// <summary>An authorization request of the public client, with PKCE.</summary>
    private string PublicClientAuthorizeUrl(
        string state,
        Dictionary<string, string?>? extraParameters = null
    )
    {
        var redirect =
            fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/') + "/signin-public";
        var issuer = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["client_id"] = "public-web",
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["redirect_uri"] = redirect,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
            ),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };
        if (extraParameters is not null)
            foreach (var (key, value) in extraParameters)
                parameters[key] = value;
        return QueryHelpers.AddQueryString(issuer + "/connect/authorize", parameters);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_or_wrong_pkce_verifier_is_rejected(bool includeWrongVerifier)
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var context = session.Context;
        var page = await context.NewPageAsync();
        var redirect =
            fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/') + "/signin-public";
        var issuer = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
        );
        await page.RouteAsync(
            "**/signin-public?**",
            route =>
                route.FulfillAsync(new() { ContentType = "text/plain", Body = "Code captured" })
        );
        var authorize = QueryHelpers.AddQueryString(
            issuer + "/connect/authorize",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = "public-web",
                ["response_type"] = "code",
                ["scope"] = "openid profile",
                ["redirect_uri"] = redirect,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = "test-state",
                ["nonce"] = Guid.NewGuid().ToString("N"),
            }
        );
        await page.GotoAsync(authorize);
        await Login(page);
        // Only the callback URL is needed; its intercepted document load state is irrelevant.
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    Regex.Escape(redirect) + "\\?",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1)
                )
            );
        var code = QueryHelpers.ParseQuery(new Uri(page.Url).Query)["code"].ToString();
        await Assert.That(code).IsNotEmpty();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = "public-web",
            ["code"] = code,
            ["redirect_uri"] = redirect,
        };
        if (includeWrongVerifier)
            fields["code_verifier"] = WebEncoders.Base64UrlEncode(
                RandomNumberGenerator.GetBytes(32)
            );
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var response = await http.PostAsync(
            "/connect/token",
            new FormUrlEncodedContent(fields)
        );
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ProtocolTests.Error(response)).IsEqualTo("invalid_grant");
    }

    private static async Task Login(IPage page, string username = "alice")
    {
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync(username);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
    }
}
