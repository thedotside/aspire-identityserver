using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Playwright;
using TUnit.Assertions.Enums;

namespace Aspire.IdentityServer.Tests;

/// <summary>
/// The simulated second factor of provider A: a level requested with acr_values asks for the
/// code 111111 declared in the sample AppHost, then the tokens carry that level.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class SimulatedMfaTests(AppFixture fixture)
{
    private const string Level = "urn:sample:loa:mfa";
    private const string HighLevel = "urn:sample:loa:high";

    private string Origin => fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');

    private string Provider => fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');

    [Test]
    public async Task The_code_is_asked_after_the_password_and_the_tokens_carry_the_level()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await SignInWithCodeAsync(page);
        var me = await Json(session.Context, Origin + "/me/mfa");
        await Assert.That(me.GetProperty("acr").GetString()).IsEqualTo(Level);
        await Assert.That(Values(me, "amr")).IsEquivalentTo(["pwd", "otp", "mfa"]);
    }

    [Test]
    public async Task A_wrong_code_is_refused_then_the_right_code_signs_in()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/login/mfa");
        await Login(page);
        await ExpectCodePageAsync(page);
        await EnterCodeAsync(page, "000000");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Invalid code.");
        await ExpectCodePageAsync(page);
        await EnterCodeAsync(page, "111111");
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var me = await Json(session.Context, Origin + "/me/mfa");
        await Assert.That(me.GetProperty("acr").GetString()).IsEqualTo(Level);
    }

    [Test]
    public async Task Cancel_on_the_code_page_answers_access_denied()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await CaptureCallbackAsync(page);
        await page.GotoAsync(AuthorizeUrl(Level));
        await Login(page);
        await ExpectCodePageAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        var query = await CallbackQueryAsync(page);
        await Assert.That(query["error"].ToString()).IsEqualTo("access_denied");
        await Assert.That(query.ContainsKey("code")).IsFalse();
    }

    [Test]
    public async Task Cancel_on_the_code_page_brings_the_user_back_to_the_application()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/login/mfa");
        await Login(page);
        await ExpectCodePageAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    "^" + Regex.Escape(Origin + "/") + "(\\?.*)?$",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1)
                )
            );
        await Assertions.Expect(page.Locator("#user-mfa")).ToHaveTextAsync("Signed out");
    }

    [Test]
    public async Task A_session_without_the_level_goes_straight_to_the_code()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var before = await Json(session.Context, Origin + "/me/a");
        var loginPages = CountNavigations(page, "/Account/Login");
        await page.GotoAsync(Origin + "/login/mfa");
        await ExpectCodePageAsync(page);
        await EnterCodeAsync(page, "111111");
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var after = await Json(session.Context, Origin + "/me/mfa");
        await Assert.That(loginPages()).IsEqualTo(0);
        await Assert.That(after.GetProperty("acr").GetString()).IsEqualTo(Level);
        // Step-up keeps the provider session: both applications see the same session id.
        await Assert
            .That(after.GetProperty("sid").GetString())
            .IsEqualTo(before.GetProperty("sid").GetString());
    }

    [Test]
    public async Task A_session_with_the_level_is_not_asked_again()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await SignInWithCodeAsync(page);
        // The application forgets its user; the provider session keeps the level.
        await session.Context.ClearCookiesAsync(new() { Name = "aspire-sample-mfa" });
        var codePages = CountNavigations(page, "/Account/Mfa");
        await page.GotoAsync(Origin + "/login/mfa");
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        await Assert.That(codePages()).IsEqualTo(0);
        var me = await Json(session.Context, Origin + "/me/mfa");
        await Assert.That(me.GetProperty("acr").GetString()).IsEqualTo(Level);
    }

    [Test]
    public async Task Signing_out_forgets_the_level()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await SignInWithCodeAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out mfa", Exact = true })
            .ClickAsync();
        // Whether the provider confirms the sign-out depends on its id_token_hint handling.
        var confirm = page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true });
        await Assertions.Expect(confirm.Or(page.Locator("#user-mfa"))).ToBeVisibleAsync();
        if (await confirm.IsVisibleAsync())
        {
            await confirm.ClickAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Return to application" })
                .ClickAsync();
        }
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        await page.GotoAsync(Origin + "/login/mfa");
        await Assertions.Expect(page.GetByLabel("Password")).ToBeVisibleAsync();
        await Login(page);
        await ExpectCodePageAsync(page);
    }

    [Test]
    public async Task Prompt_none_without_the_level_answers_interaction_required()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        await CaptureCallbackAsync(page);
        await page.GotoAsync(AuthorizeUrl(Level, prompt: "none"));
        var query = await CallbackQueryAsync(page);
        await Assert.That(query["error"].ToString()).IsEqualTo("interaction_required");
    }

    [Test]
    public async Task Mixed_acr_values_select_the_first_declared_level()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        await CaptureCallbackAsync(page);
        await page.GotoAsync(AuthorizeUrl("urn:unknown " + HighLevel));
        await ExpectCodePageAsync(page);
        await EnterCodeAsync(page, "111111");
        await Assert.That((await CallbackQueryAsync(page))["code"].ToString()).IsNotEmpty();
        // The session reached the high level: asking for it again needs no code.
        var codePages = CountNavigations(page, "/Account/Mfa");
        await page.GotoAsync(AuthorizeUrl(HighLevel));
        await Assert.That((await CallbackQueryAsync(page))["code"].ToString()).IsNotEmpty();
        await Assert.That(codePages()).IsEqualTo(0);
    }

    [Test]
    public async Task Another_declared_level_asks_for_the_code_again()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await SignInWithCodeAsync(page);
        await CaptureCallbackAsync(page);
        await page.GotoAsync(AuthorizeUrl(HighLevel));
        await ExpectCodePageAsync(page);
    }

    [Test]
    public async Task A_client_without_acr_values_is_never_asked_for_a_code()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var codePages = CountNavigations(page, "/Account/Mfa");
        await page.GotoAsync(Origin + "/login/a");
        await Login(page);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        await Assert.That(codePages()).IsEqualTo(0);
    }

    [Test]
    public async Task The_code_page_requires_an_authorization_request_for_a_declared_level()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        using (var direct = await http.GetAsync("/Account/Mfa?ReturnUrl=%2F"))
            await Assert.That(direct.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        // A valid authorization request that asks for no level does not open the code page.
        var request = new Uri(AuthorizeUrl(acrValues: null));
        var returnUrl = "/connect/authorize/callback" + request.Query;
        using var withoutLevel = await http.GetAsync(
            "/Account/Mfa?ReturnUrl=" + Uri.EscapeDataString(returnUrl)
        );
        await Assert.That(withoutLevel.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Discovery_lists_the_declared_levels()
    {
        using var http = fixture.App.CreateHttpClient("idp-a", "https");
        var discovery = await http.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        await Assert
            .That(Values(discovery, "acr_values_supported"))
            .IsEquivalentTo([Level, HighLevel], CollectionOrdering.Matching);
        using var legacy = fixture.App.CreateHttpClient("idp-legacy", "https");
        var plain = await legacy.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        await Assert.That(plain.TryGetProperty("acr_values_supported", out _)).IsFalse();
    }

    /// <summary>Signs in through the "mfa" scheme of the web sample: password, then code.</summary>
    private async Task SignInWithCodeAsync(IPage page)
    {
        await page.GotoAsync(Origin + "/login/mfa");
        await Login(page);
        await ExpectCodePageAsync(page);
        await EnterCodeAsync(page, "111111");
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
    }

    private async Task ExpectCodePageAsync(IPage page)
    {
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    "^" + Regex.Escape(Provider + "/Account/Mfa"),
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1)
                )
            );
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Two-step verification" }))
            .ToBeVisibleAsync();
    }

    private static async Task EnterCodeAsync(IPage page, string code)
    {
        await page.GetByLabel("Code").FillAsync(code);
        await page.GetByRole(AriaRole.Button, new() { Name = "Verify", Exact = true }).ClickAsync();
    }

    private static async Task Login(IPage page, string username = "alice")
    {
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync(username);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
    }

    /// <summary>
    /// An authorization request of the public client of provider A, with PKCE. Its callback is
    /// captured: only the authorization response in its URL is needed.
    /// </summary>
    private string AuthorizeUrl(string? acrValues, string? prompt = null)
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["client_id"] = "public-web",
            ["response_type"] = "code",
            ["scope"] = "openid profile",
            ["redirect_uri"] = Origin + "/signin-public",
            ["code_challenge"] = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
            ),
            ["code_challenge_method"] = "S256",
            ["state"] = "mfa-state",
            ["nonce"] = Guid.NewGuid().ToString("N"),
        };
        if (acrValues is not null)
            parameters["acr_values"] = acrValues;
        if (prompt is not null)
            parameters["prompt"] = prompt;
        return QueryHelpers.AddQueryString(Provider + "/connect/authorize", parameters);
    }

    private static Task CaptureCallbackAsync(IPage page) =>
        page.RouteAsync("**/signin-public?**", route =>
            route.FulfillAsync(new() { ContentType = "text/plain", Body = "Callback captured" })
        );

    private async Task<Dictionary<string, StringValues>> CallbackQueryAsync(IPage page)
    {
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    Regex.Escape(Origin + "/signin-public") + "\\?",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1)
                )
            );
        return QueryHelpers.ParseQuery(new Uri(page.Url).Query);
    }

    /// <summary>Counts the main-frame navigations of the page to a path, from now on.</summary>
    private static Func<int> CountNavigations(IPage page, string path)
    {
        var count = 0;
        page.FrameNavigated += (_, frame) =>
        {
            if (
                ReferenceEquals(frame, page.MainFrame)
                && string.Equals(
                    new Uri(frame.Url).AbsolutePath,
                    path,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                Interlocked.Increment(ref count);
        };
        return () => Volatile.Read(ref count);
    }

    private static string[] Values(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(v => v.GetString()!).ToArray();

    private static async Task<JsonElement> Json(IBrowserContext context, string address)
    {
        var response = await context.APIRequest.GetAsync(address);
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
}
