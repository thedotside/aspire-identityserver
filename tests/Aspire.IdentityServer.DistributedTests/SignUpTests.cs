using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Aspire.IdentityServer.Tests;

/// <summary>
/// The sign-up form of provider A, opened with prompt=create: the sample AppHost declares it with
/// the email, given_name and family_name claims. Providers B and legacy declare none.
/// </summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class SignUpTests(AppFixture fixture)
{
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

    private static string[] Values(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(v => v.GetString()!).ToArray();
}
