using System.Collections.Concurrent;
using System.Net;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

/// <summary>The provider's own pages: their assets, their policy and what they tell the user.</summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class ProviderPageTests(AppFixture fixture)
{
    [Test]
    public async Task Provider_assets_are_served_with_their_media_types()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using var stylesheet = await http.GetAsync("/Account/site.css");
        await Assert.That(stylesheet.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(stylesheet.Content.Headers.ContentType?.MediaType).IsEqualTo("text/css");
        // Browsers apply nosniff: a wrong media type would drop the stylesheet or the icon.
        await Assert
            .That(string.Join(' ', stylesheet.Headers.GetValues("X-Content-Type-Options")))
            .IsEqualTo("nosniff");
        await Assert
            .That(await stylesheet.Content.ReadAsStringAsync())
            .Contains("prefers-color-scheme: dark");
        using var icon = await http.GetAsync("/Account/icon.svg");
        await Assert.That(icon.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(icon.Content.Headers.ContentType?.MediaType).IsEqualTo("image/svg+xml");
        await Assert.That(await icon.Content.ReadAsStringAsync()).StartsWith("<svg");
    }

    [Test]
    public async Task Every_page_state_is_styled_without_a_policy_violation()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        // Each document records the CSP violations it raises, read back once it has loaded.
        await page.AddInitScriptAsync(
            "window.cspViolations = []; document.addEventListener('securitypolicyviolation', "
                + "e => window.cspViolations.push(e.violatedDirective + ' ' + e.blockedURI));"
        );
        // Chromium also reports refused styles, scripts and media types as console errors.
        var errors = new ConcurrentQueue<string>();
        page.Console += (_, message) =>
        {
            if (string.Equals(message.Type, "error", StringComparison.Ordinal))
                errors.Enqueue(message.Text);
        };
        page.PageError += (_, error) => errors.Enqueue(error);
        var provider = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');

        await page.GotoAsync(provider + "/Account/Login?ReturnUrl=%2F");
        await AssertStyled(page);

        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("not-the-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("Invalid username or password.");
        await AssertStyled(page);

        await page.GotoAsync(provider + "/connect/endsession");
        await AssertStyled(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Signed out" }))
            .ToBeVisibleAsync();
        await AssertStyled(page);

        await page.GotoAsync(UnknownClientAuthorizeUrl());
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Identity request rejected" }))
            .ToBeVisibleAsync();
        await AssertStyled(page);

        // The simulated MFA page, after the password and after a wrong code.
        await page.GotoAsync(StepUpAuthorizeUrl());
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("alice");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Two-step verification" }))
            .ToBeVisibleAsync();
        await AssertStyled(page);
        await page.GetByLabel("Code").FillAsync("000000");
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Invalid code.");
        await AssertStyled(page);

        // The sign-up page, then a refused username.
        await page.GotoAsync(SignUpAuthorizeUrl());
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create an account" }))
            .ToBeVisibleAsync();
        await AssertStyled(page);
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("Passw0rd!");
        await page.GetByLabel("email", new() { Exact = true }).FillAsync("ada@example.test");
        await page.GetByLabel("given_name", new() { Exact = true }).FillAsync("Ada");
        await page.GetByLabel("family_name", new() { Exact = true }).FillAsync("Lovelace");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("This username is already taken.");
        await AssertStyled(page);

        await Assert.That(errors.ToArray()).IsEmpty();
    }

    [Test]
    public async Task The_sign_in_page_names_the_requesting_application()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync(origin + "/login/a");
        var application = page.GetByText("web", new() { Exact = true });
        await Assertions.Expect(application).ToBeVisibleAsync();
        // A rejected attempt renders the page again, still for the same application.
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("not-the-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("Invalid username or password.");
        await Assertions.Expect(application).ToBeVisibleAsync();
    }

    [Test]
    public async Task Sign_in_focuses_the_field_left_to_type()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var provider = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');
        await page.GotoAsync(provider + "/Account/Login?ReturnUrl=%2F");
        await Assertions.Expect(page.GetByLabel("Username")).ToBeFocusedAsync();
        // As with login_hint, a known username leaves the password to type.
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("not-the-password");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByLabel("Password")).ToBeFocusedAsync();
    }

    [Test]
    public async Task A_blank_username_is_refused_like_a_wrong_password()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var provider = fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');
        await page.GotoAsync(provider + "/Account/Login?ReturnUrl=%2F");
        // Spaces pass the browser's required check; model binding turns them into null.
        await page.GetByLabel("Username").FillAsync("   ");
        await page.GetByLabel("Password").FillAsync("alice");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToContainTextAsync("Invalid username or password.");
    }

    [Test]
    public async Task The_error_page_shows_the_protocol_error()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(UnknownClientAuthorizeUrl());
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Identity request rejected" }))
            .ToBeVisibleAsync();
        await Assertions
            .Expect(page.GetByText("unauthorized_client", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Assertions
            .Expect(page.GetByText("Unknown client or client not enabled"))
            .ToBeVisibleAsync();
    }

    /// <summary>
    /// The page has loaded, its stylesheet applies despite nosniff and the CSP, and the page
    /// raised no CSP violation.
    /// </summary>
    private static async Task AssertStyled(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.Load);
        var rules = await page.EvaluateAsync<int>(
            "() => Array.from(document.styleSheets).reduce((count, sheet) => count + sheet.cssRules.length, 0)"
        );
        await Assert.That(rules).IsGreaterThan(0);
        var violations = await page.EvaluateAsync<string[]>("() => window.cspViolations");
        await Assert.That(violations).IsEmpty();
    }

    /// <summary>A request of the public client that asks provider A for its simulated second factor.</summary>
    private string StepUpAuthorizeUrl() =>
        QueryHelpers.AddQueryString(
            fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/')
                + "/connect/authorize",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = "public-web",
                ["response_type"] = "code",
                ["scope"] = "openid",
                ["redirect_uri"] =
                    fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/')
                    + "/signin-public",
                // The RFC 7636 example challenge: this request never reaches the token endpoint.
                ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                ["code_challenge_method"] = "S256",
                ["acr_values"] = "urn:sample:loa:mfa",
            }
        );

    /// <summary>A request of the public client that opens provider A's sign-up page.</summary>
    private string SignUpAuthorizeUrl() =>
        QueryHelpers.AddQueryString(
            fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/')
                + "/connect/authorize",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = "public-web",
                ["response_type"] = "code",
                ["scope"] = "openid",
                ["redirect_uri"] =
                    fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/')
                    + "/signin-public",
                // The RFC 7636 example challenge: this request never reaches the token endpoint.
                ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
                ["code_challenge_method"] = "S256",
                ["prompt"] = "create",
            }
        );

    /// <summary>A request the provider cannot return to a client: it shows its error page.</summary>
    private string UnknownClientAuthorizeUrl() =>
        QueryHelpers.AddQueryString(
            fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/')
                + "/connect/authorize",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = "unknown-client",
                ["response_type"] = "code",
                ["scope"] = "openid",
                ["redirect_uri"] = "https://localhost/signin-unknown",
            }
        );
}
