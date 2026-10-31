using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aspire.IdentityServer;

// Shared source: neither package needs a dependency on the other package.
internal static class RsaJwk
{
    internal static (RSAParameters Parameters, string KeyId) ReadPrivateBase64(string value) =>
        Read(value, privateKey: true);

    internal static (RSAParameters Parameters, string KeyId) ReadPublic(string value) =>
        Read(value, privateKey: false);

    private static (RSAParameters Parameters, string KeyId) Read(string value, bool privateKey)
    {
        try
        {
            // Do not pass untrusted private JSON to IdentityModel's JSON constructor:
            // its diagnostics can include the entire input, even in inner exceptions.
            var json = privateKey
                ? new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value))
                : value;
            using var document = JsonDocument.Parse(json);
            var key = document.RootElement;
            if (key.ValueKind != JsonValueKind.Object)
                throw new FormatException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in key.EnumerateObject())
                if (!names.Add(property.Name))
                    throw new FormatException();
            if (
                !string.Equals(Text(key, "kty"), "RSA", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(Text(key, "kid"))
                || (
                    key.TryGetProperty("alg", out _)
                    && !string.Equals(Text(key, "alg"), "RS256", StringComparison.Ordinal)
                )
                || (
                    key.TryGetProperty("use", out _)
                    && !string.Equals(Text(key, "use"), "sig", StringComparison.Ordinal)
                )
                || key.TryGetProperty("oth", out _)
            )
                throw new FormatException();
            if (
                !privateKey
                && new[] { "d", "p", "q", "dp", "dq", "qi" }.Any(p => key.TryGetProperty(p, out _))
            )
                throw new FormatException();
            var parameters = new RSAParameters
            {
                Modulus = Component(key, "n"),
                Exponent = Component(key, "e"),
                D = privateKey ? Component(key, "d") : null,
                P = privateKey ? Component(key, "p") : null,
                Q = privateKey ? Component(key, "q") : null,
                DP = privateKey ? Component(key, "dp") : null,
                DQ = privateKey ? Component(key, "dq") : null,
                InverseQ = privateKey ? Component(key, "qi") : null,
            };
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportParameters(parameters);
            }
            catch
            {
                Clear(parameters);
                throw;
            }
            if (rsa.KeySize < 2048)
            {
                Clear(parameters);
                throw new FormatException();
            }
            if (privateKey)
            {
                using var publicKey = RSA.Create();
                publicKey.ImportParameters(rsa.ExportParameters(false));
                var challenge = RandomNumberGenerator.GetBytes(32);
                var signature = rsa.SignData(
                    challenge,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                );
                if (
                    !publicKey.VerifyData(
                        challenge,
                        signature,
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1
                    )
                )
                {
                    Clear(parameters);
                    throw new FormatException();
                }
            }
            return (parameters, Text(key, "kid"));
        }
        catch (Exception error)
            when (error
                    is ArgumentException
                        or FormatException
                        or JsonException
                        or InvalidOperationException
                        or CryptographicException
            )
        {
            // Deliberately omit the original exception and all supplied values.
            throw new ArgumentException(
                privateKey
                    ? "Supply Base64(UTF8(private RSA JWK)) with a kid and a complete key of at least 2048 bits, for RS256 signing."
                    : "Supply a public RSA JWK with a kid and a key of at least 2048 bits, for RS256 signing.",
                nameof(value)
            );
        }
    }

    private static string Text(JsonElement key, string name) =>
        key.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException();

    private static byte[] Component(JsonElement key, string name)
    {
        var value = Text(key, name);
        if (
            value.Length == 0
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
        )
            throw new FormatException();
        var bytes = Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '=')
        );
        if (!string.Equals(Encode(bytes), value, StringComparison.Ordinal))
            throw new FormatException();
        return bytes;
    }

    /// <summary>Wipes the private components of exported or parsed RSA parameters.</summary>
    internal static void Clear(RSAParameters parameters)
    {
        foreach (
            var component in new[]
            {
                parameters.D,
                parameters.P,
                parameters.Q,
                parameters.DP,
                parameters.DQ,
                parameters.InverseQ,
            }
        )
            if (component is not null)
                CryptographicOperations.ZeroMemory(component);
    }

    internal static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Serialize(RSAParameters parameters, string keyId)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["kid"] = keyId,
            ["alg"] = "RS256",
            ["use"] = "sig",
            ["n"] = Encode(parameters.Modulus!),
            ["e"] = Encode(parameters.Exponent!),
        };
        if (parameters.D is not null)
        {
            fields["d"] = Encode(parameters.D);
            fields["p"] = Encode(parameters.P!);
            fields["q"] = Encode(parameters.Q!);
            fields["dp"] = Encode(parameters.DP!);
            fields["dq"] = Encode(parameters.DQ!);
            fields["qi"] = Encode(parameters.InverseQ!);
        }
        return JsonSerializer.Serialize(fields);
    }
}
