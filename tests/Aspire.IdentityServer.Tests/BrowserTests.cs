using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Xunit;

namespace Aspire.IdentityServer.Tests;

[Collection("app")]
public sealed class BrowserTests(AppFixture fixture)
{
    [Theory]
    [InlineData("a", "Alice A")]
    [InlineData("b", "Alice B")]
    [InlineData("public", "Alice A")]
    public async Task Browser_can_sign_in_with_pkce_and_sign_out(string provider, string name)
    {
        await using var context = await fixture.Browser.NewContextAsync(
            new() { IgnoreHTTPSErrors = true }
        );
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        await page.GotoAsync($"{origin}/login/{provider}");
        await Login(page);
        await page.WaitForURLAsync(origin + "/");
        Assert.Equal(name, await page.Locator($"#user-{provider}").InnerTextAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = $"Sign out {provider}", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Return to application" }).ClickAsync();
        await page.WaitForURLAsync(origin + "/");
        Assert.Equal("Signed out", await page.Locator($"#user-{provider}").InnerTextAsync());
        await page.GotoAsync($"{origin}/login/{provider}");
        await Assertions.Expect(page.GetByLabel("Username")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Two_provider_sessions_coexist_and_sign_out_independently()
    {
        await using var context = await fixture.Browser.NewContextAsync(
            new() { IgnoreHTTPSErrors = true }
        );
        var page = await context.NewPageAsync();
        var origin = fixture.App.GetEndpoint("web", "https").ToString().TrimEnd('/');
        foreach (var provider in new[] { "a", "b" })
        {
            await page.GotoAsync($"{origin}/login/{provider}");
            await Login(page);
            await page.WaitForURLAsync(origin + "/");
        }
        Assert.Equal("Alice A", await page.Locator("#user-a").InnerTextAsync());
        Assert.Equal("Alice B", await page.Locator("#user-b").InnerTextAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out a", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Return to application" }).ClickAsync();
        await page.WaitForURLAsync(origin + "/");
        Assert.Equal("Signed out", await page.Locator("#user-a").InnerTextAsync());
        Assert.Equal("Alice B", await page.Locator("#user-b").InnerTextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_wrong_pkce_verifier_is_rejected(bool includeWrongVerifier)
    {
        await using var context = await fixture.Browser.NewContextAsync(
            new() { IgnoreHTTPSErrors = true }
        );
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
        // Only the callback URL is needed; the intercepted document need not finish loading.
        await page.WaitForURLAsync(
            "**/signin-public?**",
            new() { WaitUntil = WaitUntilState.Commit }
        );
        var code = QueryHelpers.ParseQuery(new Uri(page.Url).Query)["code"].ToString();
        Assert.NotEmpty(code);
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
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(
            (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("error", out _)
        );
    }

    private static async Task Login(IPage page)
    {
        await page.GetByLabel("Username").FillAsync("alice");
        await page.GetByLabel("Password").FillAsync("alice");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true })
            .ClickAsync();
    }
}
