using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Testing;

/// <summary>
/// Client settings and tokens of the local identity providers of a started
/// <see cref="DistributedApplication" />, for integration tests. Tokens come from the providers'
/// real protocol endpoints: user sign-in uses authorization code + PKCE, never a password grant.
/// </summary>
public static partial class IdentityServerTestingExtensions
{
    /// <summary>
    /// The settings of a registered client with resolved credentials, as the default
    /// <c>WithIdentityClient</c> projection gives them to a consumer.
    /// </summary>
    /// <param name="app">The started application.</param>
    /// <param name="provider">The name of the identity provider resource.</param>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="cancellationToken">Cancels the resolution of secret parameters.</param>
    public static async Task<IdentityClientOptions> GetIdentityClientOptionsAsync(
        this DistributedApplication app,
        string provider,
        string clientId,
        CancellationToken cancellationToken = default
    ) =>
        await OptionsAsync(Client(Provider(app, provider), clientId), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Requests a client-credentials token for a registered machine client.</summary>
    /// <param name="app">The started application.</param>
    /// <param name="provider">The name of the identity provider resource.</param>
    /// <param name="clientId">The machine client identifier.</param>
    /// <param name="scope">The requested scopes; defaults to every scope registered for the client.</param>
    /// <param name="cancellationToken">Cancels discovery and the token request.</param>
    public static async Task<IdentityTokens> GetClientCredentialsTokensAsync(
        this DistributedApplication app,
        string provider,
        string clientId,
        string? scope = null,
        CancellationToken cancellationToken = default
    )
    {
        var client = Client(Provider(app, provider), clientId);
        if (!string.Equals(client.GrantType, "client_credentials", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Client '{clientId}' is not a client-credentials client.",
                nameof(clientId)
            );
        var options = await OptionsAsync(client, cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient();
        var metadata = await DiscoverAsync(http, options.Authority, cancellationToken)
            .ConfigureAwait(false);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope ?? options.Scopes,
        };
        return await RequestTokensAsync(http, metadata, fields, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Signs a test user in through the provider's authorization code + PKCE flow and its sign-in
    /// form, without a browser, then redeems the code as the client does. The client's callback
    /// is never called: its application does not need to run.
    /// </summary>
    /// <param name="app">The started application.</param>
    /// <param name="provider">The name of the identity provider resource.</param>
    /// <param name="clientId">An authorization-code client; its first redirect URI is used.</param>
    /// <param name="username">A test user of the provider; its secret password is read from the AppHost.</param>
    /// <param name="scope">The requested scopes; defaults to every scope registered for the client.</param>
    /// <param name="acrValues">
    /// The requested authentication levels, sent as <c>acr_values</c>. When the provider asks for
    /// the code of its simulated MFA, the helper types the code declared in the AppHost.
    /// </param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public static async Task<IdentityTokens> GetUserTokensAsync(
        this DistributedApplication app,
        string provider,
        string clientId,
        string username,
        string? scope = null,
        string? acrValues = null,
        CancellationToken cancellationToken = default
    )
    {
        var server = Provider(app, provider);
        var client = Client(server, clientId);
        if (
            !string.Equals(client.GrantType, "authorization_code", StringComparison.Ordinal)
            || client.RegisteredRedirectUris.Count == 0
        )
            throw new ArgumentException(
                $"Client '{clientId}' is not an authorization-code client with a redirect URI.",
                nameof(clientId)
            );
        var user = server.RegisteredUsers.SingleOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.Ordinal)
        )
            ?? throw new ArgumentException(
                $"Provider '{server.Name}' has no test user '{username}'.",
                nameof(username)
            );
        var password = await user
            .Password.Resource.GetValueAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Test user '{username}' has no password.");
        var options = await OptionsAsync(client, cancellationToken).ConfigureAwait(false);
        var redirectUri = await client
            .RegisteredRedirectUris[0]
            .GetValueAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Client '{clientId}' has no redirect URI.");

        // A fresh cookie jar per sign-in: no provider session is shared between calls.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        using var http = new HttpClient(handler);
        var metadata = await DiscoverAsync(http, options.Authority, cancellationToken)
            .ConfigureAwait(false);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["client_id"] = client.ClientId,
            ["response_type"] = "code",
            ["scope"] = scope ?? options.Scopes,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = Base64Url(RandomNumberGenerator.GetBytes(16)),
        };
        if (!string.IsNullOrWhiteSpace(acrValues))
            parameters["acr_values"] = acrValues;
        var authorize = new Uri(
            QueryHelpers.AddQueryString(metadata.AuthorizationEndpoint, parameters)
        );
        var login = await RedirectAsync(http, authorize, cancellationToken).ConfigureAwait(false);
        if (!login.AbsolutePath.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The provider refused the authorization request of '{clientId}': it redirected to {login.AbsolutePath}."
            );
        string form;
        using (var page = await http.GetAsync(login, cancellationToken).ConfigureAwait(false))
        {
            page.EnsureSuccessStatusCode();
            form = await page.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ReturnUrl"] = Field(form, "ReturnUrl"),
            ["__RequestVerificationToken"] = Field(form, "__RequestVerificationToken"),
            ["Username"] = username,
            ["Password"] = password,
            ["Button"] = "login",
        };
        Uri signedIn;
        using (var content = new FormUrlEncodedContent(credentials))
        using (
            var response = await http.PostAsync(login, content, cancellationToken)
                .ConfigureAwait(false)
        )
            signedIn = Location(login, response, $"the sign-in of test user '{username}'");
        var callback = await RedirectAsync(http, signedIn, cancellationToken).ConfigureAwait(false);
        // A requested level that the session lacks: the provider asks for its simulated MFA code.
        if (IsSimulatedMfaPage(callback, login))
            callback = await TypeSimulatedMfaCodeAsync(
                    http,
                    server,
                    callback,
                    username,
                    cancellationToken
                )
                .ConfigureAwait(false);
        var result = QueryHelpers.ParseQuery(callback.Query);
        if (result.TryGetValue("error", out var error))
            throw new InvalidOperationException(
                $"The provider returned '{error}' to client '{clientId}': {result["error_description"]}"
            );
        if (!string.Equals(result["state"], state, StringComparison.Ordinal))
            throw new InvalidOperationException("The authorization response has another state.");
        // RFC 9207: a response that names its issuer must name this provider.
        if (
            result.TryGetValue("iss", out var issuer)
            && !string.Equals(issuer, metadata.Issuer, StringComparison.Ordinal)
        )
            throw new InvalidOperationException("The authorization response names another issuer.");
        var redeem = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = result["code"].ToString(),
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        };
        return await RequestTokensAsync(http, metadata, redeem, options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Signs a test user in without <c>acr_values</c>. Kept for applications compiled against
    /// an earlier version; new code calls the overload that accepts <c>acrValues</c>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<IdentityTokens> GetUserTokensAsync(
        this DistributedApplication app,
        string provider,
        string clientId,
        string username,
        string? scope,
        CancellationToken cancellationToken
    ) =>
        app.GetUserTokensAsync(
            provider,
            clientId,
            username,
            scope,
            acrValues: null,
            cancellationToken
        );

    private static IdentityServerResource Provider(DistributedApplication app, string provider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<IdentityServerResource>()
            .SingleOrDefault(r =>
                string.Equals(r.Name, provider, StringComparison.OrdinalIgnoreCase)
            )
            ?? throw new ArgumentException(
                $"The application has no identity provider '{provider}'.",
                nameof(provider)
            );
    }

    private static IdentityClient Client(IdentityServerResource server, string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return server.RegisteredClients.SingleOrDefault(c =>
            string.Equals(c.ClientId, clientId, StringComparison.Ordinal)
        )
            ?? throw new ArgumentException(
                $"Provider '{server.Name}' has no client '{clientId}'.",
                nameof(clientId)
            );
    }

    private static async Task<IdentityClientOptions> OptionsAsync(
        IdentityClient client,
        CancellationToken cancellationToken
    ) =>
        new()
        {
            Authority = client.Authority.Url,
            ClientId = client.ClientId,
            AuthenticationMethod = client.AuthenticationMethod,
            ClientSecret = client.ClientSecret is { } secret
                ? await secret.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                : null,
            PrivateKeyPem = client.PrivateKeyPem is { } pem
                ? await pem.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                : null,
            PrivateKeyBase64 = client.PrivateKeyBase64 is { } base64
                ? await base64.Resource.GetValueAsync(cancellationToken).ConfigureAwait(false)
                : null,
            KeyId = client.KeyId,
            Scopes = string.Join(' ', client.Scopes),
            TokenExchangeAudience = client.TokenExchangeAudience,
        };

    private static async Task<Metadata> DiscoverAsync(
        HttpClient http,
        string authority,
        CancellationToken cancellationToken
    )
    {
        var document = await http.GetFromJsonAsync<JsonElement>(
                authority.TrimEnd('/') + "/.well-known/openid-configuration",
                cancellationToken
            )
            .ConfigureAwait(false);
        return new Metadata(
            Text(document, "issuer") ?? throw new InvalidOperationException("No issuer."),
            Text(document, "authorization_endpoint")
                ?? throw new InvalidOperationException("No authorization endpoint."),
            Text(document, "token_endpoint")
                ?? throw new InvalidOperationException("No token endpoint.")
        );
    }

    /// <summary>Authenticates the client as its projection says, then reads the token answer.</summary>
    private static async Task<IdentityTokens> RequestTokensAsync(
        HttpClient http,
        Metadata metadata,
        Dictionary<string, string> fields,
        IdentityClientOptions client,
        CancellationToken cancellationToken
    )
    {
        fields["client_id"] = client.ClientId;
        if (string.Equals(client.AuthenticationMethod, "private_key_jwt", StringComparison.Ordinal))
        {
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, metadata.Issuer);
        }
        else if (
            string.Equals(
                client.AuthenticationMethod,
                "client_secret_post",
                StringComparison.Ordinal
            )
        )
            fields["client_secret"] = client.ClientSecret
                ?? throw new InvalidOperationException(
                    $"Client '{client.ClientId}' has no secret."
                );
        using var content = new FormUrlEncodedContent(
            fields.Where(f => !string.IsNullOrWhiteSpace(f.Value))
        );
        using var response = await http.PostAsync(
                metadata.TokenEndpoint,
                content,
                cancellationToken
            )
            .ConfigureAwait(false);
        var payload = await response
            .Content.ReadFromJsonAsync<JsonElement>(cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The token endpoint answered {(int)response.StatusCode} {Text(payload, "error")}: {Text(payload, "error_description")}"
                ),
                null,
                response.StatusCode
            );
        return new IdentityTokens
        {
            AccessToken = Text(payload, "access_token")
                ?? throw new InvalidOperationException("The token answer has no access token."),
            TokenType = Text(payload, "token_type") ?? "Bearer",
            IdToken = Text(payload, "id_token"),
            RefreshToken = Text(payload, "refresh_token"),
            Scope = Text(payload, "scope"),
            ExpiresIn =
                payload.TryGetProperty("expires_in", out var expiresIn)
                && expiresIn.ValueKind == JsonValueKind.Number
                && expiresIn.TryGetInt32(out var seconds)
                    ? seconds
                    : null,
        };
    }

    private static async Task<Uri> RedirectAsync(
        HttpClient http,
        Uri address,
        CancellationToken cancellationToken
    )
    {
        using var response = await http.GetAsync(address, cancellationToken).ConfigureAwait(false);
        return Location(address, response, address.GetLeftPart(UriPartial.Path));
    }

    private static Uri Location(Uri address, HttpResponseMessage response, string step)
    {
        if ((int)response.StatusCode is < 300 or > 399 || response.Headers.Location is not { } next)
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected a redirect after {step}, got {(int)response.StatusCode}."
                )
            );
        return next.IsAbsoluteUri ? next : new Uri(address, next);
    }

    /// <summary>The verification page of the provider's simulated MFA.</summary>
    private static bool IsSimulatedMfaPage(Uri address, Uri login) =>
        string.Equals(
            address.GetLeftPart(UriPartial.Authority),
            login.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase
        ) && address.AbsolutePath.StartsWith("/Account/Mfa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Types the code declared with <c>WithSimulatedMfa</c>, then follows the authorization
    /// request back to the client.
    /// </summary>
    private static async Task<Uri> TypeSimulatedMfaCodeAsync(
        HttpClient http,
        IdentityServerResource server,
        Uri page,
        string username,
        CancellationToken cancellationToken
    )
    {
        var mfa = server.SimulatedMfa
            ?? throw new InvalidOperationException(
                $"Provider '{server.Name}' asked for a simulated MFA code that the AppHost does not declare."
            );
        string form;
        using (var response = await http.GetAsync(page, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            form = await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ReturnUrl"] = Field(form, "ReturnUrl"),
            ["__RequestVerificationToken"] = Field(form, "__RequestVerificationToken"),
            ["Code"] = mfa.Code,
            ["Button"] = "verify",
        };
        Uri verified;
        using (var content = new FormUrlEncodedContent(fields))
        using (
            var response = await http.PostAsync(page, content, cancellationToken)
                .ConfigureAwait(false)
        )
            verified = Location(
                page,
                response,
                $"the simulated MFA code of test user '{username}'"
            );
        return await RedirectAsync(http, verified, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The value of a named input of the provider's sign-in form.</summary>
    private static string Field(string html, string name)
    {
        foreach (Match input in InputPattern().Matches(html))
            if (
                NamePattern().Match(input.Value) is { Success: true } inputName
                && string.Equals(inputName.Groups["name"].Value, name, StringComparison.Ordinal)
                && ValuePattern().Match(input.Value) is { Success: true } value
            )
                return WebUtility.HtmlDecode(value.Groups["value"].Value);
        throw new InvalidOperationException($"The sign-in page has no '{name}' field.");
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InputPattern();

    [GeneratedRegex(
        "\\bname=\"(?<name>[^\"]*)\"",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex NamePattern();

    [GeneratedRegex(
        "\\bvalue=\"(?<value>[^\"]*)\"",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ValuePattern();

    private sealed record Metadata(
        string Issuer,
        string AuthorizationEndpoint,
        string TokenEndpoint
    );
}
