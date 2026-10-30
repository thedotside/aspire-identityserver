using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Client;

/// <summary>Creates a fresh short-lived assertion for each token request.</summary>
public static class PrivateKeyJwt
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <param name="audience">The audience required by the provider, usually its token endpoint.</param>
    public static string CreateAssertion(IdentityClientOptions client, string audience)
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
        using var rsa = RSA.Create();
        var key = CreateSigningKey(client, rsa);
        key.CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false };
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
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
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            }
        );
    }

    private static SecurityKey CreateSigningKey(IdentityClientOptions client, RSA rsa)
    {
        if (client.PrivateKeyBase64 is { } value)
        {
            var (parameters, keyId) = RsaJwk.ReadPrivateBase64(value);
            if (
                client.KeyId is not null
                && !string.Equals(client.KeyId, keyId, StringComparison.Ordinal)
            )
                throw new ArgumentException(
                    "KeyId must match the private JWK kid.",
                    nameof(client)
                );
            // Set validated components directly: parsing private JSON with JsonWebKey(string)
            // can disclose that JSON in IdentityModel diagnostics and inner exceptions.
            return new JsonWebKey
            {
                Kty = "RSA",
                Kid = keyId,
                Alg = "RS256",
                Use = "sig",
                N = RsaJwk.Encode(parameters.Modulus!),
                E = RsaJwk.Encode(parameters.Exponent!),
                D = RsaJwk.Encode(parameters.D!),
                P = RsaJwk.Encode(parameters.P!),
                Q = RsaJwk.Encode(parameters.Q!),
                DP = RsaJwk.Encode(parameters.DP!),
                DQ = RsaJwk.Encode(parameters.DQ!),
                QI = RsaJwk.Encode(parameters.InverseQ!),
            };
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(client.PrivateKeyPem, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(client.KeyId, nameof(client));
        try
        {
            rsa.ImportFromPem(client.PrivateKeyPem);
            // Exporting private parameters also rejects a public-only PEM.
            _ = rsa.ExportParameters(true);
            if (rsa.KeySize < 2048)
                throw new CryptographicException();
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            throw new ArgumentException(
                "Supply a private RSA PEM key of at least 2048 bits.",
                nameof(client)
            );
        }
        return new RsaSecurityKey(rsa) { KeyId = client.KeyId };
    }
}
