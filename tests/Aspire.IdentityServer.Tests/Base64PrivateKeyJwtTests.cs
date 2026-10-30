using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Client;
using Aspire.IdentityServer.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Aspire.IdentityServer.Tests;

public sealed class Base64PrivateKeyJwtTests
{
    [Fact]
    public async Task Generated_pair_projects_only_a_secret_reference_and_registers_the_matching_public_key()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("base64-idp");
        server.AddApiResource("orders").WithScopes("read");
        var client = server
            .AddClientCredentialsClient("worker")
            .WithGeneratedBase64PrivateKeyJwt()
            .WithScopes("read");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, "OAuth")
            .WithIdentityClient(client, p => p.PrivateKeyBase64("Custom:Key").KeyId("Custom:Kid"));
        var environment = await ProjectionTests.Evaluate(consumer);
        Assert.Same(client.PrivateKeyBase64!.Resource, environment["OAuth__PrivateKeyBase64"]);
        Assert.Same(client.PrivateKeyBase64.Resource, environment["Custom__Key"]);
        Assert.True(client.PrivateKeyBase64.Resource.Secret);
        Assert.Null(client.PrivateKeyPem);
        Assert.False(environment.ContainsKey("OAuth__PrivateKeyPem"));
        Assert.IsType<EndpointReference>(environment["OAuth__Authority"]);
        Assert.Equal(client.KeyId, environment["OAuth__KeyId"]);
        Assert.Contains(
            consumer.Resource.Annotations.OfType<WaitAnnotation>(),
            a => ReferenceEquals(a.Resource, server.Resource)
        );
        var value = (await client.PrivateKeyBase64.Resource.GetValueAsync(default))!;
        using var privateDocument = JsonDocument.Parse(Convert.FromBase64String(value));
        using var publicDocument = JsonDocument.Parse(client.PublicJwk!);
        var privateKey = privateDocument.RootElement;
        var publicKey = publicDocument.RootElement;
        Assert.Equal("RS256", privateKey.GetProperty("alg").GetString());
        Assert.Equal("sig", privateKey.GetProperty("use").GetString());
        Assert.Equal("RSA", privateKey.GetProperty("kty").GetString());
        foreach (var field in new[] { "kid", "n", "e" })
            Assert.Equal(
                publicKey.GetProperty(field).GetString(),
                privateKey.GetProperty(field).GetString()
            );
        foreach (var field in new[] { "n", "e", "d", "p", "q", "dp", "dq", "qi" })
        {
            var component = privateKey.GetProperty(field).GetString()!;
            Assert.True(component.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
            if (field is not "n" and not "e")
                Assert.False(publicKey.TryGetProperty(field, out _));
        }
        Assert.True(new JsonWebKey(privateKey.GetRawText()).KeySize >= 2048);
        var provider = await ProjectionTests.Evaluate(server);
        Assert.DoesNotContain(
            provider.Keys,
            k => k.Contains("PrivateKey", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            provider.Values,
            v => ReferenceEquals(v, client.PrivateKeyBase64.Resource)
        );
        Assert.Equal(client.PublicJwk, provider["IdentityServer__Clients__0__PublicJwk"]);
        var leaked = provider
            .Values.OfType<string>()
            .Any(v => v.Contains(value, StringComparison.Ordinal));
        Assert.False(leaked); // A failing assertion must not print private input.
        await ValidateAssertion(
            new IdentityClientOptions
            {
                ClientId = "worker",
                PrivateKeyBase64 = value,
                KeyId = client.KeyId,
            },
            client.PublicJwk!
        );
    }

    [Fact]
    public async Task Imported_pair_preserves_its_kid_and_signs_without_a_separate_KeyId()
    {
        using var rsa = RSA.Create(2048);
        var value = Encode(rsa, "external-key");
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var client = builder
            .AddIdentityServer("import-idp")
            .AddClientCredentialsClient("worker")
            .WithBase64PrivateKeyJwt(value);
        Assert.Equal("external-key", client.KeyId);
        var matches = string.Equals(
            value,
            await client.PrivateKeyBase64!.Resource.GetValueAsync(default),
            StringComparison.Ordinal
        );
        Assert.True(matches); // Do not print credentials on failure.
        await ValidateAssertion(
            new IdentityClientOptions { ClientId = "worker", PrivateKeyBase64 = value },
            client.PublicJwk!
        );
        var withoutMetadata = JsonNode.Parse(Convert.FromBase64String(value))!.AsObject();
        withoutMetadata.Remove("alg");
        withoutMetadata.Remove("use");
        await ValidateAssertion(
            new IdentityClientOptions
            {
                ClientId = "worker",
                PrivateKeyBase64 = Encode(withoutMetadata.ToJsonString()),
            },
            client.PublicJwk!
        );
    }

    [Fact]
    public async Task Existing_secret_stays_deferred_and_must_be_marked_secret()
    {
        using var rsa = RSA.Create(2048);
        var publicJwk = Public(rsa, "external-key");
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("deferred-idp");
        var secret = builder.AddParameter(
            "external-key",
            () => throw new InvalidOperationException("Secret must stay deferred."),
            secret: true
        );
        var client = server
            .AddClientCredentialsClient("worker")
            .WithBase64PrivateKeyJwt(secret, publicJwk);
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, "OAuth");
        Assert.Same(
            secret.Resource,
            (await ProjectionTests.Evaluate(consumer))["OAuth__PrivateKeyBase64"]
        );
        Assert.Throws<ArgumentException>(() =>
            server
                .AddClientCredentialsClient("nonsecret")
                .WithBase64PrivateKeyJwt(builder.AddParameter("plain", () => "unused"), publicJwk)
        );
        Assert.Throws<ArgumentException>(() =>
            server
                .AddClientCredentialsClient("private-public-jwk")
                .WithBase64PrivateKeyJwt(
                    secret,
                    Encoding.UTF8.GetString(Convert.FromBase64String(Encode(rsa, "external-key")))
                )
        );
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("json")]
    [InlineData("utf8")]
    [InlineData("array")]
    [InlineData("public-only")]
    [InlineData("missing-kid")]
    [InlineData("missing-crt")]
    [InlineData("component")]
    [InlineData("weak")]
    [InlineData("wrong-kty")]
    [InlineData("wrong-alg")]
    [InlineData("wrong-use")]
    [InlineData("inconsistent")]
    [InlineData("duplicate")]
    public void Invalid_private_keys_are_rejected_without_disclosing_input_or_inner_exceptions(
        string change
    )
    {
        using var rsa = RSA.Create(
            string.Equals(change, "weak", StringComparison.Ordinal) ? 1024 : 2048
        );
        var json = JsonNode.Parse(Convert.FromBase64String(Encode(rsa, "test-key")))!.AsObject();
        switch (change)
        {
            case "public-only":
                foreach (var field in new[] { "d", "p", "q", "dp", "dq", "qi" })
                    json.Remove(field);
                break;
            case "missing-kid":
                json.Remove("kid");
                break;
            case "missing-crt":
                json.Remove("qi");
                break;
            case "component":
                json["d"] = "private-material-is-invalid!";
                break;
            case "wrong-kty":
                json["kty"] = "EC";
                break;
            case "wrong-alg":
                json["alg"] = "RS512";
                break;
            case "wrong-use":
                json["use"] = "enc";
                break;
            case "inconsistent":
                using (var other = RSA.Create(2048))
                    json["n"] = JsonNode.Parse(Convert.FromBase64String(Encode(other, "other")))![
                        "n"
                    ]!.GetValue<string>();
                break;
        }
        var raw = json.ToJsonString();
        var value = change switch
        {
            "base64" => "invalid-private-base64!",
            "json" => Encode(raw + "private-json-tail"),
            "utf8" => Convert.ToBase64String(new byte[] { 0xff, 0xfe }),
            "array" => Encode("[\"private-array\"]"),
            "duplicate" => Encode(raw[..^1] + ",\"kid\":\"duplicate\"}"),
            _ => Encode(raw),
        };
        var options = new IdentityClientOptions { ClientId = "worker", PrivateKeyBase64 = value };
        var error = Assert.Throws<ArgumentException>(() =>
            PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
        );
        AssertSafe(error, value, raw);
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var client = builder.AddIdentityServer("invalid-idp").AddClientCredentialsClient("worker");
        AssertSafe(
            Assert.Throws<ArgumentException>(() => client.WithBase64PrivateKeyJwt(value)),
            value,
            raw
        );
    }

    [Fact]
    public void Pem_and_Base64_are_mutually_exclusive_and_kid_must_match_exactly()
    {
        using var rsa = RSA.Create(2048);
        var value = Encode(rsa, "test-key");
        var options = new IdentityClientOptions
        {
            ClientId = "worker",
            PrivateKeyBase64 = value,
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
        };
        AssertSafe(
            Assert.Throws<ArgumentException>(() =>
                PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
            ),
            value,
            options.PrivateKeyPem
        );
        options.PrivateKeyPem = null;
        options.KeyId = "TEST-KEY";
        AssertSafe(
            Assert.Throws<ArgumentException>(() =>
                PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
            ),
            value,
            "TEST-KEY"
        );
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("conflict-idp");
        var base64 = server.AddClientCredentialsClient("base64").WithBase64PrivateKeyJwt(value);
        Assert.Throws<InvalidOperationException>(() =>
            base64.WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem())
        );
        var pem = server
            .AddClientCredentialsClient("pem")
            .WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem());
        Assert.Throws<InvalidOperationException>(() => pem.WithBase64PrivateKeyJwt(value));
        var secret = server.AddClientCredentialsClient("secret").WithGeneratedClientSecret();
        Assert.Throws<InvalidOperationException>(() => secret.WithBase64PrivateKeyJwt(value));
    }

    private static void AssertSafe(Exception error, params string[] privateValues)
    {
        Assert.Null(error.InnerException);
        var leaked = privateValues.Any(v => error.ToString().Contains(v, StringComparison.Ordinal));
        Assert.False(leaked);
    }

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

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static async Task ValidateAssertion(IdentityClientOptions options, string publicJwk)
    {
        const string audience = "https://localhost/connect/token";
        var assertion = PrivateKeyJwt.CreateAssertion(options, audience);
        var jwt = new JsonWebToken(assertion);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal(new JsonWebKey(publicJwk).Kid, jwt.Kid);
        Assert.Equal(options.ClientId, jwt.Subject);
        Assert.InRange(
            jwt.ValidTo - jwt.IssuedAt,
            TimeSpan.FromSeconds(59),
            TimeSpan.FromSeconds(61)
        );
        Assert.NotEqual(
            jwt.Id,
            new JsonWebToken(PrivateKeyJwt.CreateAssertion(options, audience)).Id,
            StringComparer.Ordinal
        );
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            assertion,
            new TokenValidationParameters
            {
                ValidIssuer = options.ClientId,
                ValidAudience = audience,
                IssuerSigningKey = new JsonWebKey(publicJwk),
                ValidAlgorithms = ["RS256"],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
            }
        );
        Assert.True(result.IsValid);
    }
}
