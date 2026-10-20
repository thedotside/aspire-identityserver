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
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(client.PrivateKeyPem, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(client.KeyId, nameof(client));
        if (!Uri.TryCreate(audience, UriKind.Absolute, out _))
            throw new ArgumentException("Use an absolute assertion audience.", nameof(audience));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(client.PrivateKeyPem);
        if (rsa.KeySize < 2048)
            throw new ArgumentException("Use an RSA key of at least 2048 bits.", nameof(client));
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
                SigningCredentials = new SigningCredentials(
                    new RsaSecurityKey(rsa)
                    {
                        KeyId = client.KeyId,
                        CryptoProviderFactory = new CryptoProviderFactory
                        {
                            CacheSignatureProviders = false,
                        },
                    },
                    SecurityAlgorithms.RsaSha256
                ),
            }
        );
    }
}
