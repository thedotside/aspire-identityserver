using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Tests;

/// <summary>RSA test credentials in the formats accepted by Hosting and Client.</summary>
internal static class TestKeys
{
    /// <summary>Base64(UTF8(private RSA JWK JSON)).</summary>
    internal static string Encode(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(true);
        var key = JsonNode.Parse(Public(rsa, kid))!.AsObject();
        key["d"] = Base64UrlEncoder.Encode(parameters.D!);
        key["p"] = Base64UrlEncoder.Encode(parameters.P!);
        key["q"] = Base64UrlEncoder.Encode(parameters.Q!);
        key["dp"] = Base64UrlEncoder.Encode(parameters.DP!);
        key["dq"] = Base64UrlEncoder.Encode(parameters.DQ!);
        key["qi"] = Base64UrlEncoder.Encode(parameters.InverseQ!);
        return Encode(key.ToJsonString());
    }

    internal static string Public(RSA rsa, string kid)
    {
        var parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(
            new
            {
                kty = "RSA",
                kid,
                alg = "RS256",
                use = "sig",
                n = Base64UrlEncoder.Encode(parameters.Modulus!),
                e = Base64UrlEncoder.Encode(parameters.Exponent!),
            }
        );
    }

    internal static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
}
