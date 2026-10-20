using System.Security.Cryptography;
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
    public string? KeyId { get; private set; }
    public string? PublicJwk { get; private set; }
    public bool UsePkce { get; private set; } = true;
    public IReadOnlyCollection<string> Scopes => ScopeNames;
    internal string GrantType { get; }
    internal string ParameterPrefix { get; }
    internal HashSet<string> ScopeNames { get; } = new(StringComparer.Ordinal);
    internal List<ReferenceExpression> RedirectUris { get; } = [];
    internal List<ReferenceExpression> PostLogoutRedirectUris { get; } = [];

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
        if (!privateKey.Resource.Secret)
            throw new ArgumentException(
                "The private key must be an Aspire secret parameter.",
                nameof(privateKey)
            );
        using var document = JsonDocument.Parse(publicJwk);
        var key = document.RootElement;
        if (
            !string.Equals(key.GetProperty("kty").GetString(), "RSA", StringComparison.Ordinal)
            || key.TryGetProperty("d", out _)
            || new[] { "p", "q", "dp", "dq", "qi", "oth" }.Any(p => key.TryGetProperty(p, out _))
        )
            throw new ArgumentException(
                "Supply a public RSA JWK without private key material.",
                nameof(publicJwk)
            );
        var kid = key.GetProperty("kid").GetString();
        ArgumentException.ThrowIfNullOrWhiteSpace(kid, nameof(publicJwk));
        using var rsa = RSA.Create();
        rsa.ImportParameters(
            new RSAParameters
            {
                Modulus = FromBase64Url(key.GetProperty("n").GetString()!),
                Exponent = FromBase64Url(key.GetProperty("e").GetString()!),
            }
        );
        if (rsa.KeySize < 2048)
            throw new ArgumentException("Use an RSA key of at least 2048 bits.", nameof(publicJwk));
        PrivateKeyPem = privateKey;
        PublicJwk = publicJwk;
        KeyId = kid;
        AuthenticationMethod = "private_key_jwt";
        return this;
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

    private static byte[] FromBase64Url(string value) =>
        Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=')
        );
}
