using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Client;

/// <summary>Creates a fresh short-lived assertion for each token request.</summary>
public static class PrivateKeyJwt
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>The explicit JWT type of draft-ietf-oauth-rfc7523bis client assertions.</summary>
    public const string ClientAuthenticationType = "client-authentication+jwt";

    /// <summary>
    /// Creates an assertion with the draft-ietf-oauth-rfc7523bis profile, which prevents audience
    /// injection: the audience is the provider's issuer identifier alone, and the JWT is typed
    /// <c>client-authentication+jwt</c>.
    /// </summary>
    /// <param name="issuer">The <c>issuer</c> value of the provider's discovery document.</param>
    public static string CreateAssertion(IdentityClientOptions client, string issuer) =>
        Create(client, issuer, ClientAuthenticationType);

    /// <summary>
    /// Creates an assertion with the original RFC 7523 profile: the audience is the token endpoint
    /// and the JWT is typed <c>JWT</c>. Use it only for providers that refuse the issuer audience.
    /// </summary>
    /// <param name="tokenEndpoint">The <c>token_endpoint</c> of the provider's discovery document.</param>
    public static string CreateTokenEndpointAssertion(
        IdentityClientOptions client,
        string tokenEndpoint
    ) => Create(client, tokenEndpoint, JwtConstants.HeaderType);

    // One imported key per options instance and credential: options bound by IOptionsMonitor are
    // replaced on reload, and a changed credential or KeyId is imported and validated again.
    private static readonly ConditionalWeakTable<IdentityClientOptions, ImportedKey> Imported =
        new();

    private static string Create(IdentityClientOptions client, string audience, string type)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        if (client.PrivateKeyPem is not null && client.PrivateKeyBase64 is not null)
            throw new ArgumentException(
                "Configure either PrivateKeyPem or PrivateKeyBase64, not both.",
                nameof(client)
            );
        if (!Uri.TryCreate(audience, UriKind.Absolute, out _))
            throw new ArgumentException("Use an absolute assertion audience.", nameof(audience));
        var key = SigningKey(client);
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            TokenType = type,
            Issuer = client.ClientId,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(1),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = client.ClientId,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(
                key.SecurityKey,
                SecurityAlgorithms.RsaSha256
            ),
        };
        // RSA instances do not guarantee concurrent use; one signature takes about a millisecond.
        lock (key)
            return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static ImportedKey SigningKey(IdentityClientOptions client)
    {
        var credential = client.PrivateKeyBase64 ?? client.PrivateKeyPem;
        if (
            Imported.TryGetValue(client, out var cached) && cached.Matches(credential, client.KeyId)
        )
            return cached;
        var imported = Import(client);
        Imported.AddOrUpdate(client, imported);
        return imported;
    }

    private static ImportedKey Import(IdentityClientOptions client)
    {
        if (client.PrivateKeyBase64 is { } value)
        {
            var (parameters, keyId) = RsaJwk.ReadPrivateBase64(value);
            try
            {
                if (
                    client.KeyId is not null
                    && !string.Equals(client.KeyId, keyId, StringComparison.Ordinal)
                )
                    throw new ArgumentException(
                        "KeyId must match the private JWK kid.",
                        nameof(client)
                    );
                // Import the validated components directly: parsing private JSON with
                // JsonWebKey(string) can disclose that JSON in IdentityModel diagnostics.
                var rsa = RSA.Create();
                rsa.ImportParameters(parameters);
                return new ImportedKey(value, client.KeyId, rsa, keyId);
            }
            finally
            {
                RsaJwk.Clear(parameters);
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(client.PrivateKeyPem, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(client.KeyId, nameof(client));
        var pemKey = RSA.Create();
        try
        {
            pemKey.ImportFromPem(client.PrivateKeyPem);
            // Exporting private parameters also rejects a public-only PEM.
            RsaJwk.Clear(pemKey.ExportParameters(true));
            if (pemKey.KeySize < 2048)
                throw new CryptographicException();
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            pemKey.Dispose();
            throw new ArgumentException(
                "Supply a private RSA PEM key of at least 2048 bits.",
                nameof(client)
            );
        }
        return new ImportedKey(client.PrivateKeyPem, client.KeyId, pemKey, client.KeyId);
    }

    private sealed class ImportedKey(
        string credential,
        string? configuredKeyId,
        RSA rsa,
        string kid
    )
    {
        // No shared signature provider cache: it would keep the key alive after its options.
        public RsaSecurityKey SecurityKey { get; } =
            new(rsa)
            {
                KeyId = kid,
                CryptoProviderFactory = new CryptoProviderFactory
                {
                    CacheSignatureProviders = false,
                },
            };

        public bool Matches(string? currentCredential, string? currentKeyId) =>
            ReferenceEquals(credential, currentCredential)
            && string.Equals(configuredKeyId, currentKeyId, StringComparison.Ordinal);
    }
}
