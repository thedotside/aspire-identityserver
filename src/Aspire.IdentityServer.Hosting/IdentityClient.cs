using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Hosting;

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

    public IResourceBuilder<IdentityServerResource> Server { get; }
    public string ClientId { get; }
    public EndpointReference Authority => Server.GetEndpoint("https");
    public string AuthenticationMethod { get; private set; } = "none";
    public IResourceBuilder<ParameterResource>? ClientSecret { get; private set; }
    public IResourceBuilder<ParameterResource>? PrivateKeyPem { get; private set; }
    public IResourceBuilder<ParameterResource>? PrivateKeyBase64 { get; private set; }
    public string? KeyId { get; private set; }
    public string? PublicJwk { get; private set; }
    public bool UsePkce { get; private set; } = true;
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
    internal string GrantType { get; }
    internal string ParameterPrefix { get; }
    internal HashSet<string> ScopeNames { get; } = new(StringComparer.Ordinal);
    internal List<ReferenceExpression> RedirectUris { get; } = [];
    internal List<ReferenceExpression> PostLogoutRedirectUris { get; } = [];
    internal List<TokenExchangePolicy> TokenExchanges { get; } = [];

    /// <summary>
    /// Allows this machine client to exchange a same-provider authorization-code access token
    /// for this API. Requested scopes must also be present in the source token.
    /// The subject is preserved; only the named user claims are included.
    /// </summary>
    public IdentityClient WithTokenExchangeFrom(
        IdentityClient subjectClient,
        IdentityApiResource api,
        params string[] userClaims
    )
    {
        ArgumentNullException.ThrowIfNull(subjectClient);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(userClaims);
        if (!string.Equals(GrantType, "client_credentials", StringComparison.Ordinal))
            throw new InvalidOperationException("Only machine clients can enable token exchange.");
        if (!string.Equals(subjectClient.GrantType, "authorization_code", StringComparison.Ordinal))
            throw new ArgumentException(
                "The source must be an authorization-code client.",
                nameof(subjectClient)
            );
        if (
            !ReferenceEquals(Server.Resource, subjectClient.Server.Resource)
            || !ReferenceEquals(Server.Resource, api.Server.Resource)
        )
            throw new ArgumentException(
                "Token exchange currently requires the same identity provider.",
                nameof(subjectClient)
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
                userClaims.Distinct(StringComparer.Ordinal).ToArray()
            )
        );
        return this;
    }

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

    public IdentityClient WithGeneratedClientSecret()
    {
        EnsureAuthenticationUnset();
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return WithClientSecret(
            Server.ApplicationBuilder.AddParameter(
                $"{ParameterPrefix}-secret",
                () => value,
                secret: true
            )
        );
    }

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
        var key = Server.ApplicationBuilder.AddParameter(
            $"{ParameterPrefix}-key",
            () => privateKeyBase64,
            secret: true
        );
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
        var key = Server.ApplicationBuilder.AddParameter(
            $"{ParameterPrefix}-key",
            () => pem,
            secret: true
        );
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

    public IdentityClient WithRedirectUri(EndpointReference endpoint, string path)
    {
        ValidateCallback(path);
        RedirectUris.Add(ReferenceExpression.Create($"{endpoint}{path}"));
        return this;
    }

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
            !path.StartsWith('/')
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('#', StringComparison.Ordinal)
            || path.Contains('\\', StringComparison.Ordinal)
        )
            throw new ArgumentException(
                "Use an absolute callback path, for example /signin-oidc.",
                nameof(path)
            );
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
    IReadOnlyList<string> UserClaims
);
