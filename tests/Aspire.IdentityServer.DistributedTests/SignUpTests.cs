using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

/// <summary>
/// The sign-up form of provider A, opened with prompt=create: the sample AppHost declares it with
/// the email, given_name and family_name claims. Providers B and legacy declare none. Every test
/// creates its own username: accounts live as long as the shared AppHost.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class SignUpTests(AppFixture fixture)
{
    private const string Password = "Passw0rd!";

    private string Origin => fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');

    private string ProviderA => fixture.App.GetEndpoint("idp-a", "https").ToString().TrimEnd('/');

    [Test]
    public async Task Discovery_offers_create_only_with_a_sign_up_form()
    {
        using var withForm = fixture.App.CreateHttpClient("idp-a", "https");
        var offered = await withForm.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        await Assert.That(Values(offered, "prompt_values_supported")).Contains("create");
        using var withoutForm = fixture.App.CreateHttpClient("idp-b", "https");
        var plain = await withoutForm.GetFromJsonAsync<JsonElement>(
            "/.well-known/openid-configuration"
        );
        await Assert.That(Values(plain, "prompt_values_supported")).DoesNotContain("create");
    }

    [Test]
    public async Task Prompt_create_sends_the_request_to_the_sign_up_page()
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        using var response = await http.GetAsync(
            AuthorizeUrl("idp-a", "public-web", "/signin-public")
        );
        // Duende redirects to its interaction pages with 303 See Other.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        var page = new Uri(new Uri(ProviderA), response.Headers.Location!);
        await Assert.That(page.AbsolutePath).IsEqualTo("/Account/Create");
        var returnUrl = QueryHelpers.ParseQuery(page.Query)["returnUrl"].ToString();
        await Assert.That(returnUrl).StartsWith("/connect/authorize/callback?");
        // Duende marks prompt=create as handled, so that the callback resumes the request.
        await Assert.That(returnUrl).Contains("suppressed_prompt=create");
    }

    [Test]
    public async Task Without_a_sign_up_form_prompt_create_shows_the_error_page()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(AuthorizeUrl("idp-b", "web", "/signin-b"));
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Identity request rejected" }))
            .ToBeVisibleAsync();
        await Assertions
            .Expect(page.GetByText("invalid_request", new() { Exact = true }))
            .ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Unsupported prompt mode")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Prompt_create_signs_the_new_account_in_to_the_application()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var username = NewUsername();
        await page.GotoAsync(Origin + "/");
        await page.GetByRole(AriaRole.Link, new() { Name = "Create an account" }).ClickAsync();
        await ExpectSignUpPageAsync(page);
        await Assertions
            .Expect(page.GetByLabel("Password"))
            .ToHaveAttributeAsync("autocomplete", "new-password");
        await Assertions
            .Expect(page.GetByLabel("given_name", new() { Exact = true }))
            .ToHaveAttributeAsync("autocomplete", "given-name");
        await SignUpAsync(page, username);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var me = await Json(session.Context, Origin + "/me/a");
        await Assert.That(me.GetProperty("subject").GetString()).IsEqualTo($"idp-a:{username}");
        await Assert.That(me.GetProperty("name").GetString()).IsEqualTo(username);
        await Assert.That(me.GetProperty("email").GetString()).IsEqualTo("ada@example.test");
    }

    [Test]
    [Arguments("alice")]
    [Arguments("ALICE")]
    public async Task A_declared_username_is_taken_whatever_its_case(string username)
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await page.GotoAsync(Origin + "/signup/a");
        await ExpectSignUpPageAsync(page);
        await SignUpAsync(page, username);
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToHaveTextAsync("This username is already taken.");
        await ExpectSignUpPageAsync(page);
        await Assertions
            .Expect(page.GetByLabel("email", new() { Exact = true }))
            .ToHaveValueAsync("ada@example.test");
        await Assertions.Expect(page.GetByLabel("Password")).ToHaveValueAsync("");
    }

    [Test]
    public async Task A_created_username_is_taken_whatever_its_case()
    {
        var username = NewUsername();
        await using (var first = await fixture.NewBrowserSessionAsync())
        {
            var page = await first.Context.NewPageAsync();
            await page.GotoAsync(Origin + "/signup/a");
            await ExpectSignUpPageAsync(page);
            await SignUpAsync(page, username);
            await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        }
        await using var second = await fixture.NewBrowserSessionAsync();
        var other = await second.Context.NewPageAsync();
        await other.GotoAsync(Origin + "/signup/a");
        await ExpectSignUpPageAsync(other);
        await SignUpAsync(other, username.ToUpperInvariant());
        await Assertions
            .Expect(other.GetByRole(AriaRole.Alert))
            .ToHaveTextAsync("This username is already taken.");
    }

    [Test]
    public async Task A_blank_field_is_refused_without_creating_the_account()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var username = NewUsername();
        await page.GotoAsync(Origin + "/signup/a");
        await ExpectSignUpPageAsync(page);
        // Spaces pass the browser's required check: the page refuses them.
        await SignUpAsync(page, username, givenName: "   ");
        await Assertions
            .Expect(page.GetByRole(AriaRole.Alert))
            .ToHaveTextAsync("Fill in every field.");
        await Assertions.Expect(page.GetByLabel("Username")).ToHaveValueAsync(username);
        await Assertions.Expect(page.GetByLabel("Password")).ToHaveValueAsync("");
        // The refused attempt created nothing: the same username is still free.
        await page.GetByLabel("given_name", new() { Exact = true }).FillAsync("Ada");
        await page.GetByLabel("Password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true })
            .ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var me = await Json(session.Context, Origin + "/me/a");
        await Assert.That(me.GetProperty("subject").GetString()).IsEqualTo($"idp-a:{username}");
    }

    [Test]
    public async Task Cancel_on_the_sign_up_page_answers_access_denied()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        await CaptureCallbackAsync(page);
        await page.GotoAsync(AuthorizeUrl("idp-a", "public-web", "/signin-public"));
        await ExpectSignUpPageAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        var query = await CallbackQueryAsync(page);
        await Assert.That(query["error"].ToString()).IsEqualTo("access_denied");
        await Assert.That(query.ContainsKey("code")).IsFalse();
    }

    [Test]
    public async Task The_new_account_signs_in_again_with_its_password()
    {
        var username = NewUsername();
        await using (var first = await fixture.NewBrowserSessionAsync())
        {
            var page = await first.Context.NewPageAsync();
            await page.GotoAsync(Origin + "/signup/a");
            await ExpectSignUpPageAsync(page);
            await SignUpAsync(page, username);
            await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        }
        await using var second = await fixture.NewBrowserSessionAsync();
        var again = await second.Context.NewPageAsync();
        await again.GotoAsync(Origin + "/login/a");
        await SignInAsync(again, username, Password);
        await Assertions.Expect(again).ToHaveURLAsync(Origin + "/");
        var me = await Json(second.Context, Origin + "/me/a");
        await Assert.That(me.GetProperty("subject").GetString()).IsEqualTo($"idp-a:{username}");
    }

    [Test]
    public async Task An_open_session_is_replaced_by_the_new_account()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var username = NewUsername();
        await page.GotoAsync(Origin + "/login/a");
        await SignInAsync(page, "alice", "alice");
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var before = await Json(session.Context, Origin + "/me/a");
        // prompt=create comes before the sign-in: the open session does not skip the page.
        await page.GotoAsync(Origin + "/signup/a");
        await ExpectSignUpPageAsync(page);
        await SignUpAsync(page, username);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var after = await Json(session.Context, Origin + "/me/a");
        await Assert.That(after.GetProperty("subject").GetString()).IsEqualTo($"idp-a:{username}");
        await Assert
            .That(after.GetProperty("sid").GetString())
            .IsNotEqualTo(before.GetProperty("sid").GetString());
    }

    [Test]
    public async Task Prompt_create_goes_through_pushed_authorization()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var username = NewUsername();
        // The par scheme of the web sample requires pushed authorization requests.
        await page.GotoAsync(Origin + "/signup/par");
        await ExpectSignUpPageAsync(page);
        await SignUpAsync(page, username);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var me = await Json(session.Context, Origin + "/me/par");
        await Assert.That(me.GetProperty("subject").GetString()).IsEqualTo($"idp-a:{username}");
    }

    [Test]
    public async Task The_sign_up_page_requires_an_authorization_request()
    {
        using var withForm = fixture.App.CreateHttpClient("idp-a", "https");
        using (var bare = await withForm.GetAsync("/Account/Create"))
            await Assert.That(bare.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using (var root = await withForm.GetAsync("/Account/Create?ReturnUrl=%2F"))
            await Assert.That(root.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        using var withoutForm = fixture.App.CreateHttpClient("idp-b", "https");
        using var missing = await withoutForm.GetAsync("/Account/Create?ReturnUrl=%2F");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Sign_up_logs_the_account_without_its_password()
    {
        await using var session = await fixture.NewBrowserSessionAsync();
        var page = await session.Context.NewPageAsync();
        var username = NewUsername();
        var password = $"pw-{Guid.NewGuid():N}";
        await page.GotoAsync(Origin + "/signup/a");
        await ExpectSignUpPageAsync(page);
        await SignUpAsync(page, username, password);
        await Assertions.Expect(page).ToHaveURLAsync(Origin + "/");
        var created = $"Created test account '{username}'";
        var lines = await ProviderLogAsync("idp-a", created);
        await Assert
            .That(lines.Exists(l => l.Contains(created, StringComparison.Ordinal)))
            .IsTrue();
        await Assert
            .That(lines.Exists(l => l.Contains(password, StringComparison.Ordinal)))
            .IsFalse();
    }

    private static string NewUsername() => $"ada-{Guid.NewGuid():N}";

    /// <summary>Fills the sign-up form of provider A, then creates the account.</summary>
    private static async Task SignUpAsync(
        IPage page,
        string username,
        string password = Password,
        string givenName = "Ada"
    )
    {
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync(password);
        // Exact labels: a claim label is its type, and "name" would match several of them.
        await page.GetByLabel("email", new() { Exact = true }).FillAsync("ada@example.test");
        await page.GetByLabel("given_name", new() { Exact = true }).FillAsync(givenName);
        await page.GetByLabel("family_name", new() { Exact = true }).FillAsync("Lovelace");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account", Exact = true })
            .ClickAsync();
    }

    private static async Task SignInAsync(IPage page, string username, string password)
    {
        await page.GetByLabel("Username").FillAsync(username);
        await page.GetByLabel("Password").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
    }

    private async Task ExpectSignUpPageAsync(IPage page)
    {
        await Assertions
            .Expect(page)
            .ToHaveURLAsync(
                new Regex(
                    "^" + Regex.Escape(ProviderA + "/Account/Create"),
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1)
                )
            );
        await Assertions
            .Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Create an account" }))
            .ToBeVisibleAsync();
    }

    /// <summary>
    /// An authorization request with prompt=create and PKCE, from a browser client of the
    /// sample, to its callback on the web sample.
    /// </summary>
    private string AuthorizeUrl(string provider, string clientId, string callback)
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return QueryHelpers.AddQueryString(
            fixture.App.GetEndpoint(provider, "https").ToString().TrimEnd('/')
                + "/connect/authorize",
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["client_id"] = clientId,
                ["response_type"] = "code",
                ["scope"] = "openid profile",
                ["redirect_uri"] = Origin + callback,
                ["code_challenge"] = WebEncoders.Base64UrlEncode(
                    SHA256.HashData(Encoding.ASCII.GetBytes(verifier))
                ),
                ["code_challenge_method"] = "S256",
                ["state"] = "sign-up-state",
                ["nonce"] = Guid.NewGuid().ToString("N"),
                ["prompt"] = "create",
            }
        );
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

    /// <summary>
    /// The log lines of a provider, read again until one contains <paramref name="expected" />
    /// or 10 seconds pass: the AppHost captures the provider's output asynchronously.
    /// </summary>
    private async Task<List<string>> ProviderLogAsync(string provider, string expected)
    {
        var logs = fixture.App.Services.GetRequiredService<ResourceLoggerService>();
        var resource = fixture
            .App.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.Single(r => string.Equals(r.Name, provider, StringComparison.Ordinal));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var lines = new List<string>();
            await foreach (var batch in logs.GetAllAsync(resource))
                lines.AddRange(batch.Select(l => l.Content));
            if (
                lines.Exists(l => l.Contains(expected, StringComparison.Ordinal))
                || watch.Elapsed > TimeSpan.FromSeconds(10)
            )
                return lines;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
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
