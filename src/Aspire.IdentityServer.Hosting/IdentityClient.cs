using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.IdentityServer;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>A client registration and its deferred configuration values.</summary>
public sealed class IdentityClient
{
    internal IdentityClient(
        IResourceBuilder<IdentityServerResource> server,
        string clientId,
        string grantType,
        int index
    )
    {
        Server = server;
        ClientId = clientId;
        GrantType = grantType;
        ParameterPrefix = $"{server.Resource.Name}-client-{index}";
    }

    /// <summary>The provider of this client.</summary>
    public IResourceBuilder<IdentityServerResource> Server { get; }

    /// <summary>The client identifier, unique within its provider.</summary>
    public string ClientId { get; }

    /// <summary>The deferred HTTPS address of the provider, its issuer.</summary>
    public EndpointReference Authority => Server.GetEndpoint("https");

    /// <summary><c>none</c>, <c>client_secret_post</c> or <c>private_key_jwt</c>, set by the credential methods.</summary>
    public string AuthenticationMethod { get; private set; } = "none";

    /// <summary>The secret parameter of a <c>client_secret_post</c> client; the provider only receives its hash.</summary>
    public IResourceBuilder<ParameterResource>? ClientSecret { get; private set; }

    /// <summary>The secret parameter holding a PEM private key, never sent to the provider.</summary>
    public IResourceBuilder<ParameterResource>? PrivateKeyPem { get; private set; }

    /// <summary>The secret parameter holding Base64(UTF8(private RSA JWK)), never sent to the provider.</summary>
    public IResourceBuilder<ParameterResource>? PrivateKeyBase64 { get; private set; }

    /// <summary>The <c>kid</c> of the registered public key.</summary>
    public string? KeyId { get; private set; }

    /// <summary>The public RSA JWK registered with the provider.</summary>
    public string? PublicJwk { get; private set; }

    /// <summary>Whether the authorization code flow requires PKCE; always true for public clients.</summary>
    public bool UsePkce { get; private set; } = true;

    /// <summary><c>authorization_code</c> for interactive clients, <c>client_credentials</c> for machine clients.</summary>
    public string GrantType { get; }

    /// <summary>
    /// The sign-in callbacks added with <see cref="WithRedirectUri(EndpointReference, string)" /> and
    /// <see cref="WithRedirectUri(Uri)" />, in declaration order.
    /// </summary>
    public IReadOnlyList<ReferenceExpression> RegisteredRedirectUris => RedirectUris.AsReadOnly();

    /// <summary>The scopes this client may request.</summary>
    public IReadOnlyCollection<string> Scopes => ScopeNames;

    /// <summary>The exchange audience when all configured policies select the same API.</summary>
    public string? TokenExchangeAudience =>
        TokenExchanges
            .Select(p => p.Api.Audience)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray()
            is [var audience]
            ? audience
            : null;
    internal string ParameterPrefix { get; }
    internal HashSet<string> ScopeNames { get; } = new(StringComparer.Ordinal);
    internal List<ReferenceExpression> RedirectUris { get; } = [];
    internal List<ReferenceExpression> PostLogoutRedirectUris { get; } = [];
    internal List<TokenExchangePolicy> TokenExchanges { get; } = [];
    internal List<ReferenceExpression> AllowedCorsOrigins { get; } = [];
    internal int? AccessTokenLifetimeSeconds { get; private set; }
    internal int? RefreshTokenLifetimeSeconds { get; private set; }
    internal ReferenceExpression? BackChannelLogoutUri { get; private set; }
    internal ReferenceExpression? FrontChannelLogoutUri { get; private set; }

    /// <summary>
    /// Sets the lifetime of this client's access tokens, from one second to one day: five minutes
    /// by default. A short lifetime lets tests exercise expiry and renewal quickly.
    /// </summary>
    public IdentityClient WithAccessTokenLifetime(TimeSpan lifetime)
    {
        AccessTokenLifetimeSeconds = Seconds(
            lifetime,
            TimeSpan.FromDays(1),
            "one day",
            nameof(lifetime)
        );
        return this;
    }

    /// <summary>
    /// Sets the absolute lifetime of the refresh tokens of an interactive client that requests
    /// <c>offline_access</c>, from one second to 30 days, which is the default.
    /// </summary>
    public IdentityClient WithRefreshTokenLifetime(TimeSpan lifetime)
    {
        EnsureInteractive("Only interactive clients receive refresh tokens.");
        RefreshTokenLifetimeSeconds = Seconds(
            lifetime,
            TimeSpan.FromDays(30),
            "30 days",
            nameof(lifetime)
        );
        return this;
    }

    /// <summary>
    /// Allows a browser application served at this endpoint's origin to call the provider's
    /// discovery, token and UserInfo endpoints with CORS, as a single-page application does.
    /// </summary>
    public IdentityClient WithAllowedCorsOrigin(EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureInteractive("Only interactive clients run in a browser.");
        AllowedCorsOrigins.Add(ReferenceExpression.Create($"{endpoint}"));
        return this;
    }

    /// <summary>
    /// Adds a literal sign-in callback for a tool outside the AppHost: an HTTPS address, an HTTP
    /// loopback address, or the private-use scheme of a native application (RFC 8252).
    /// </summary>
    public IdentityClient WithRedirectUri(Uri redirectUri)
    {
        RedirectUris.Add(Literal(redirectUri, nameof(redirectUri)));
        return this;
    }

    /// <summary>Adds a literal sign-out callback, with the rules of <see cref="WithRedirectUri(Uri)" />.</summary>
    public IdentityClient WithPostLogoutRedirectUri(Uri postLogoutRedirectUri)
    {
        PostLogoutRedirectUris.Add(Literal(postLogoutRedirectUri, nameof(postLogoutRedirectUri)));
        return this;
    }

    /// <summary>
    /// Sends a logout token to the deferred <paramref name="endpoint" /> address followed by
    /// <paramref name="path" /> when the user's provider session ends (OpenID Connect
    /// Back-Channel Logout 1.0).
    /// </summary>
    public IdentityClient WithBackChannelLogoutUri(EndpointReference endpoint, string path)
    {
        ValidateCallback(path);
        BackChannelLogoutUri = ReferenceExpression.Create($"{endpoint}{path}");
        return this;
    }

    /// <summary>
    /// Loads the deferred <paramref name="endpoint" /> address followed by <paramref name="path" />
    /// in a frame of the provider's sign-out page (OpenID Connect Front-Channel Logout 1.0).
    /// </summary>
    public IdentityClient WithFrontChannelLogoutUri(EndpointReference endpoint, string path)
    {
        ValidateCallback(path);
        FrontChannelLogoutUri = ReferenceExpression.Create($"{endpoint}{path}");
        return this;
    }

    /// <summary>
    /// Allows this machine client to exchange an authorization-code access token for this API.
    /// Requested scopes must also be present in the source token. With a source client of this
    /// provider, the subject is preserved; with a source client of another provider, this
    /// declares a directional trust and the local subject is <c>{source provider}|{sub}</c>.
    /// Only the named user claims are included.
    /// </summary>
    public IdentityClient WithTokenExchangeFrom(
        IdentityClient subjectClient,
        IdentityApiResource api,
        params string[] userClaims
    ) =>
        WithTokenExchangeFrom(subjectClient, api, TokenExchangeScopes.FromSubjectToken, userClaims);

    /// <summary>
    /// Allows this machine client to exchange a same-provider authorization-code access token
    /// for this API, with the scope rule chosen by <paramref name="scopes" />.
    /// The subject is preserved; only the named user claims are included.
    /// </summary>
    public IdentityClient WithTokenExchangeFrom(
        IdentityClient subjectClient,
        IdentityApiResource api,
        TokenExchangeScopes scopes,
        params string[] userClaims
    )
    {
        ArgumentNullException.ThrowIfNull(subjectClient);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(userClaims);
        if (!Enum.IsDefined(scopes))
            throw new ArgumentOutOfRangeException(nameof(scopes));
        if (!string.Equals(GrantType, "client_credentials", StringComparison.Ordinal))
            throw new InvalidOperationException("Only machine clients can enable token exchange.");
        if (!string.Equals(subjectClient.GrantType, "authorization_code", StringComparison.Ordinal))
            throw new ArgumentException(
                "The source must be an authorization-code client.",
                nameof(subjectClient)
            );
        // The source client may belong to another provider, whose tokens this provider then
        // trusts for this policy only; the issued token always targets an API of this provider.
        if (!ReferenceEquals(Server.Resource, api.Server.Resource))
            throw new ArgumentException(
                "The exchanged token targets an API of this machine client's provider.",
                nameof(api)
            );
        if (userClaims.Any(c => !TokenExchangeClaims.IsUserClaim(c)))
            throw new ArgumentException(
                "Select user claims, not protocol claims such as sub, scope, aud or act.",
                nameof(userClaims)
            );
        if (
            TokenExchanges.Any(p =>
                ReferenceEquals(p.SubjectClient, subjectClient) && ReferenceEquals(p.Api, api)
            )
        )
            throw new InvalidOperationException(
                "This token exchange policy is already configured."
            );
        TokenExchanges.Add(
            new TokenExchangePolicy(
                subjectClient,
                api,
                scopes,
                userClaims.Distinct(StringComparer.Ordinal).ToArray()
            )
        );
        return this;
    }

    /// <summary>
    /// Adds scopes: declared API scopes, or for interactive clients <c>openid</c>, <c>profile</c>,
    /// <c>email</c>, declared identity scopes and <c>offline_access</c>, which issues refresh tokens.
    /// </summary>
    public IdentityClient WithScopes(params string[] scopes)
    {
        foreach (var scope in scopes)
        {
            ValidateScope(scope);
            ScopeNames.Add(scope);
        }
        return this;
    }

    /// <summary>PKCE is independent of the client authentication method.</summary>
    public IdentityClient RequirePkce(bool required = true)
    {
        if (!string.Equals(GrantType, "authorization_code", StringComparison.Ordinal))
            throw new InvalidOperationException("PKCE only applies to authorization code clients.");
        UsePkce = required;
        return this;
    }

    /// <summary>Authenticates with <c>client_secret_post</c>, using an existing Aspire secret parameter.</summary>
    public IdentityClient WithClientSecret(IResourceBuilder<ParameterResource> secret)
    {
        EnsureAuthenticationUnset();
        if (!secret.Resource.Secret)
            throw new ArgumentException(
                "The client secret must be an Aspire secret parameter.",
                nameof(secret)
            );
        ClientSecret = secret;
        AuthenticationMethod = "client_secret_post";
        return this;
    }

    /// <summary>Authenticates with <c>client_secret_post</c>, using a random 32-byte secret generated at registration.</summary>
    public IdentityClient WithGeneratedClientSecret()
    {
        EnsureAuthenticationUnset();
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return WithClientSecret(GeneratedParameter("secret", () => value));
    }

    /// <summary>Authenticates with <c>private_key_jwt</c>, using an RSA 2048 PEM key generated at registration.</summary>
    public IdentityClient WithGeneratedPrivateKeyJwt()
    {
        using var rsa = RSA.Create(2048);
        return WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Generates RSA 2048 credentials as Base64(UTF8(private JWK JSON)).</summary>
    public IdentityClient WithGeneratedBase64PrivateKeyJwt()
    {
        EnsureAuthenticationUnset();
        using var rsa = RSA.Create(2048);
        var keyId = Convert
            .ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))
            .ToLowerInvariant();
        var parameters = rsa.ExportParameters(true);
        try
        {
            return WithBase64PrivateKeyJwt(
                Convert.ToBase64String(Encoding.UTF8.GetBytes(RsaJwk.Serialize(parameters, keyId)))
            );
        }
        finally
        {
            RsaJwk.Clear(parameters);
        }
    }

    /// <summary>Imports Base64(UTF8(private RSA JWK JSON)); registers only the public key.</summary>
    public IdentityClient WithBase64PrivateKeyJwt(string privateKeyBase64)
    {
        EnsureAuthenticationUnset();
        var (parameters, keyId) = RsaJwk.ReadPrivateBase64(privateKeyBase64);
        // Only the public half is registered; the private components are not kept.
        RsaJwk.Clear(parameters);
        var publicParameters = new RSAParameters
        {
            Modulus = parameters.Modulus,
            Exponent = parameters.Exponent,
        };
        var key = GeneratedParameter("key", () => privateKeyBase64);
        return WithBase64PrivateKeyJwt(key, RsaJwk.Serialize(publicParameters, keyId));
    }

    /// <summary>
    /// Projects an existing secret containing Base64(UTF8(private RSA JWK JSON)).
    /// The public JWK must match; the secret remains deferred and is not read here.
    /// </summary>
    public IdentityClient WithBase64PrivateKeyJwt(
        IResourceBuilder<ParameterResource> privateKey,
        string publicJwk
    )
    {
        EnsureAuthenticationUnset();
        RegisterPublicKey(privateKey, publicJwk);
        PrivateKeyBase64 = privateKey;
        return this;
    }

    /// <summary>Uses an RSA private key; only its public JWK is sent to the identity provider.</summary>
    public IdentityClient WithPrivateKeyJwt(string privateKeyPem)
    {
        EnsureAuthenticationUnset();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        if (rsa.KeySize < 2048)
            throw new ArgumentException(
                "Use an RSA key of at least 2048 bits.",
                nameof(privateKeyPem)
            );
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var parameters = rsa.ExportParameters(false);
        var keyId = Convert
            .ToHexString(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))
            .ToLowerInvariant();
        var jwk = JsonSerializer.Serialize(
            new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = keyId,
                n = Base64Url(parameters.Modulus!),
                e = Base64Url(parameters.Exponent!),
            }
        );
        var key = GeneratedParameter("key", () => pem);
        return WithPrivateKeyJwt(key, jwk);
    }

    /// <summary>Projects an existing secret key parameter and registers its matching public JWK.</summary>
    public IdentityClient WithPrivateKeyJwt(
        IResourceBuilder<ParameterResource> privateKey,
        string publicJwk
    )
    {
        EnsureAuthenticationUnset();
        RegisterPublicKey(privateKey, publicJwk);
        PrivateKeyPem = privateKey;
        return this;
    }

    /// <summary>A secret generated for this client, named after it and grouped under its provider.</summary>
    private IResourceBuilder<ParameterResource> GeneratedParameter(
        string suffix,
        Func<string> value
    ) =>
        Server
            .ApplicationBuilder.AddParameter(
                IdentityServerExtensions.ParameterName(
                    Server,
                    ClientId,
                    suffix,
                    $"{ParameterPrefix}-{suffix}"
                ),
                value,
                secret: true
            )
            .WithParentRelationship(Server);

    private void RegisterPublicKey(IResourceBuilder<ParameterResource> privateKey, string publicJwk)
    {
        if (!privateKey.Resource.Secret)
            throw new ArgumentException(
                "The private key must be an Aspire secret parameter.",
                nameof(privateKey)
            );
        var (parameters, keyId) = RsaJwk.ReadPublic(publicJwk);
        PublicJwk = RsaJwk.Serialize(parameters, keyId);
        KeyId = keyId;
        AuthenticationMethod = "private_key_jwt";
    }

    /// <summary>Adds a sign-in callback: the deferred <paramref name="endpoint" /> address followed by <paramref name="path" />.</summary>
    public IdentityClient WithRedirectUri(EndpointReference endpoint, string path)
    {
        ValidateCallback(path);
        RedirectUris.Add(ReferenceExpression.Create($"{endpoint}{path}"));
        return this;
    }

    /// <summary>Adds a sign-out callback: the deferred <paramref name="endpoint" /> address followed by <paramref name="path" />.</summary>
    public IdentityClient WithPostLogoutRedirectUri(EndpointReference endpoint, string path)
    {
        ValidateCallback(path);
        PostLogoutRedirectUris.Add(ReferenceExpression.Create($"{endpoint}{path}"));
        return this;
    }

    private void ValidateCallback(string path)
    {
        if (!string.Equals(GrantType, "authorization_code", StringComparison.Ordinal))
            throw new InvalidOperationException("Only interactive clients have redirect URIs.");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (
            !path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
        )
            throw new ArgumentException(
                "Use an absolute callback path, for example /signin-oidc.",
                nameof(path)
            );
    }

    private void EnsureInteractive(string message)
    {
        if (!string.Equals(GrantType, "authorization_code", StringComparison.Ordinal))
            throw new InvalidOperationException(message);
    }

    private ReferenceExpression Literal(Uri address, string parameter)
    {
        ArgumentNullException.ThrowIfNull(address, parameter);
        EnsureInteractive("Only interactive clients have redirect URIs.");
        var allowed =
            address.IsAbsoluteUri
            && string.IsNullOrEmpty(address.Fragment)
            && string.IsNullOrEmpty(address.UserInfo)
            && (
                string.Equals(address.Scheme, "https", StringComparison.Ordinal)
                || (
                    string.Equals(address.Scheme, "http", StringComparison.Ordinal)
                    && address.IsLoopback
                )
                // RFC 8252, section 7.1: a reverse domain name, such as com.example.app.
                || (address.Scheme.Contains('.', StringComparison.Ordinal) && !address.IsLoopback)
            );
        if (!allowed)
            throw new ArgumentException(
                "Use an HTTPS address, an HTTP loopback address or a private-use scheme such as com.example.app, without a fragment.",
                parameter
            );
        return ReferenceExpression.Create($"{address.OriginalString}");
    }

    private static int Seconds(TimeSpan lifetime, TimeSpan maximum, string most, string parameter)
    {
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime > maximum)
            throw new ArgumentOutOfRangeException(
                parameter,
                $"Use a lifetime from one second to {most}."
            );
        return (int)Math.Ceiling(lifetime.TotalSeconds);
    }

    internal static void ValidateScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (scope.Any(c => c < 0x21 || c > 0x7e || c is '"' or '\\'))
            throw new ArgumentException(
                "A scope must be a single OAuth scope token.",
                nameof(scope)
            );
    }

    private void EnsureAuthenticationUnset()
    {
        if (!string.Equals(AuthenticationMethod, "none", StringComparison.Ordinal))
            throw new InvalidOperationException("Client authentication is already configured.");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record TokenExchangePolicy(
    IdentityClient SubjectClient,
    IdentityApiResource Api,
    TokenExchangeScopes Scopes,
    IReadOnlyList<string> UserClaims
);

/// <summary>Where a token exchange takes the scopes it may grant.</summary>
public enum TokenExchangeScopes
{
    /// <summary>
    /// The user's token must already target the API and carry each requested scope: the exchange
    /// only narrows it. This is the default.
    /// </summary>
    FromSubjectToken,

    /// <summary>
    /// The machine client may request any scope of the API it is registered for, even absent
    /// from the user's token, whose audience is not checked. The user's token still proves who
    /// signed in, through the authorized interactive client; the machine client is responsible
    /// for choosing the scopes appropriate to that user.
    /// </summary>
    FromMachineClient,
}
