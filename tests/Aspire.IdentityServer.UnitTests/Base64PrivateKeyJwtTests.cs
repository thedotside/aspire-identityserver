using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aspire.IdentityServer.Tests;

public sealed class Base64PrivateKeyJwtTests
{
    [Test]
    public async Task Generated_pair_projects_only_a_secret_reference_and_registers_the_matching_public_key()
    {
        var builder = TestApp.CreateBuilder();
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
        await Assert
            .That(environment["OAuth__PrivateKeyBase64"])
            .IsSameReferenceAs(client.PrivateKeyBase64!.Resource);
        await Assert
            .That(environment["Custom__Key"])
            .IsSameReferenceAs(client.PrivateKeyBase64.Resource);
        await Assert.That(client.PrivateKeyBase64.Resource.Secret).IsTrue();
        await Assert.That(client.PrivateKeyPem).IsNull();
        await Assert.That(environment).DoesNotContainKey("OAuth__PrivateKeyPem");
        await Assert.That(environment["OAuth__Authority"]).IsTypeOf<EndpointReference>();
        await Assert.That(environment["OAuth__KeyId"]).IsEqualTo(client.KeyId);
        await Assert
            .That(consumer.Resource.Annotations.OfType<WaitAnnotation>())
            .Contains(a => ReferenceEquals(a.Resource, server.Resource));
        var value = (await client.PrivateKeyBase64.Resource.GetValueAsync(default))!;
        using var privateDocument = JsonDocument.Parse(Convert.FromBase64String(value));
        using var publicDocument = JsonDocument.Parse(client.PublicJwk!);
        var privateKey = privateDocument.RootElement;
        var publicKey = publicDocument.RootElement;
        await Assert.That(privateKey.GetProperty("alg").GetString()).IsEqualTo("RS256");
        await Assert.That(privateKey.GetProperty("use").GetString()).IsEqualTo("sig");
        await Assert.That(privateKey.GetProperty("kty").GetString()).IsEqualTo("RSA");
        foreach (var field in new[] { "kid", "n", "e" })
            await Assert
                .That(privateKey.GetProperty(field).GetString())
                .IsEqualTo(publicKey.GetProperty(field).GetString());
        foreach (var field in new[] { "n", "e", "d", "p", "q", "dp", "dq", "qi" })
        {
            var component = privateKey.GetProperty(field).GetString()!;
            await Assert
                .That(component.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                .IsTrue();
            if (field is not "n" and not "e")
                await Assert.That(publicKey.TryGetProperty(field, out _)).IsFalse();
        }
        await Assert.That(new JsonWebKey(privateKey.GetRawText()).KeySize >= 2048).IsTrue();
        var provider = await ProjectionTests.Evaluate(server);
        await Assert
            .That(provider.Keys)
            .DoesNotContain(k => k.Contains("PrivateKey", StringComparison.Ordinal));
        await Assert
            .That(provider.Values)
            .DoesNotContain(v => ReferenceEquals(v, client.PrivateKeyBase64.Resource));
        await Assert
            .That(provider["IdentityServer__Clients__0__PublicJwk"])
            .IsEqualTo(client.PublicJwk);
        var leaked = provider
            .Values.OfType<string>()
            .Any(v => v.Contains(value, StringComparison.Ordinal));
        await Assert.That(leaked).IsFalse(); // A failing assertion must not print private input.
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

    [Test]
    public async Task Imported_pair_preserves_its_kid_and_signs_without_a_separate_KeyId()
    {
        using var rsa = RSA.Create(2048);
        var value = TestKeys.Encode(rsa, "external-key");
        var builder = TestApp.CreateBuilder();
        var client = builder
            .AddIdentityServer("import-idp")
            .AddClientCredentialsClient("worker")
            .WithBase64PrivateKeyJwt(value);
        await Assert.That(client.KeyId).IsEqualTo("external-key");
        var matches = string.Equals(
            value,
            await client.PrivateKeyBase64!.Resource.GetValueAsync(default),
            StringComparison.Ordinal
        );
        await Assert.That(matches).IsTrue(); // Do not print credentials on failure.
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
                PrivateKeyBase64 = TestKeys.Encode(withoutMetadata.ToJsonString()),
            },
            client.PublicJwk!
        );
    }

    [Test]
    public async Task Existing_secret_stays_deferred_and_must_be_marked_secret()
    {
        using var rsa = RSA.Create(2048);
        var publicJwk = TestKeys.Public(rsa, "external-key");
        var builder = TestApp.CreateBuilder();
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
        await Assert
            .That((await ProjectionTests.Evaluate(consumer))["OAuth__PrivateKeyBase64"])
            .IsSameReferenceAs(secret.Resource);
        await Assert
            .That(() =>
                server
                    .AddClientCredentialsClient("nonsecret")
                    .WithBase64PrivateKeyJwt(
                        builder.AddParameter("plain", () => "unused"),
                        publicJwk
                    )
            )
            .ThrowsExactly<ArgumentException>();
        await Assert
            .That(() =>
                server
                    .AddClientCredentialsClient("private-public-jwk")
                    .WithBase64PrivateKeyJwt(
                        secret,
                        Encoding.UTF8.GetString(
                            Convert.FromBase64String(TestKeys.Encode(rsa, "external-key"))
                        )
                    )
            )
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments("base64")]
    [Arguments("json")]
    [Arguments("utf8")]
    [Arguments("array")]
    [Arguments("public-only")]
    [Arguments("missing-kid")]
    [Arguments("missing-crt")]
    [Arguments("component")]
    [Arguments("weak")]
    [Arguments("wrong-kty")]
    [Arguments("wrong-alg")]
    [Arguments("wrong-use")]
    [Arguments("inconsistent")]
    [Arguments("duplicate")]
    public async Task Invalid_private_keys_are_rejected_without_disclosing_input_or_inner_exceptions(
        string change
    )
    {
        using var rsa = RSA.Create(
            string.Equals(change, "weak", StringComparison.Ordinal) ? 1024 : 2048
        );
        var json = JsonNode
            .Parse(Convert.FromBase64String(TestKeys.Encode(rsa, "test-key")))!
            .AsObject();
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
                    json["n"] = JsonNode.Parse(
                        Convert.FromBase64String(TestKeys.Encode(other, "other"))
                    )!["n"]!.GetValue<string>();
                break;
        }
        var raw = json.ToJsonString();
        var value = change switch
        {
            "base64" => "invalid-private-base64!",
            "json" => TestKeys.Encode(raw + "private-json-tail"),
            "utf8" => Convert.ToBase64String(new byte[] { 0xff, 0xfe }),
            "array" => TestKeys.Encode("[\"private-array\"]"),
            "duplicate" => TestKeys.Encode(raw[..^1] + ",\"kid\":\"duplicate\"}"),
            _ => TestKeys.Encode(raw),
        };
        var options = new IdentityClientOptions { ClientId = "worker", PrivateKeyBase64 = value };
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
        );
        await AssertSafe(error, value, raw);
        var builder = TestApp.CreateBuilder();
        var client = builder.AddIdentityServer("invalid-idp").AddClientCredentialsClient("worker");
        await AssertSafe(
            Assert.ThrowsExactly<ArgumentException>(() => client.WithBase64PrivateKeyJwt(value)),
            value,
            raw
        );
    }

    [Test]
    public async Task Pem_and_Base64_are_mutually_exclusive_and_kid_must_match_exactly()
    {
        using var rsa = RSA.Create(2048);
        var value = TestKeys.Encode(rsa, "test-key");
        var options = new IdentityClientOptions
        {
            ClientId = "worker",
            PrivateKeyBase64 = value,
            PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
        };
        await AssertSafe(
            Assert.ThrowsExactly<ArgumentException>(() =>
                PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
            ),
            value,
            options.PrivateKeyPem
        );
        options.PrivateKeyPem = null;
        options.KeyId = "TEST-KEY";
        await AssertSafe(
            Assert.ThrowsExactly<ArgumentException>(() =>
                PrivateKeyJwt.CreateAssertion(options, "https://localhost/connect/token")
            ),
            value,
            "TEST-KEY"
        );
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("conflict-idp");
        var base64 = server.AddClientCredentialsClient("base64").WithBase64PrivateKeyJwt(value);
        await Assert
            .That(() => base64.WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem()))
            .ThrowsExactly<InvalidOperationException>();
        var pem = server
            .AddClientCredentialsClient("pem")
            .WithPrivateKeyJwt(rsa.ExportPkcs8PrivateKeyPem());
        await Assert
            .That(() => pem.WithBase64PrivateKeyJwt(value))
            .ThrowsExactly<InvalidOperationException>();
        var secret = server.AddClientCredentialsClient("secret").WithGeneratedClientSecret();
        await Assert
            .That(() => secret.WithBase64PrivateKeyJwt(value))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task An_imported_key_is_reused_until_its_credential_or_KeyId_changes()
    {
        using var first = RSA.Create(2048);
        using var second = RSA.Create(2048);
        var options = new IdentityClientOptions
        {
            ClientId = "worker",
            PrivateKeyBase64 = TestKeys.Encode(first, "first-key"),
        };
        var assertions = await Task.WhenAll(
            Enumerable
                .Range(0, 32)
                .Select(_ =>
                    Task.Run(() => PrivateKeyJwt.CreateAssertion(options, "https://idp.test"))
                )
        );
        foreach (var assertion in assertions)
            await Assert.That(await IsSignedBy(assertion, first, "first-key")).IsTrue();
        await Assert.That(assertions.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(32);

        options.PrivateKeyBase64 = TestKeys.Encode(second, "second-key");
        var rotated = PrivateKeyJwt.CreateAssertion(options, "https://idp.test");
        await Assert.That(await IsSignedBy(rotated, second, "second-key")).IsTrue();
        await Assert.That(await IsSignedBy(rotated, first, "second-key")).IsFalse();

        options.KeyId = "another-kid";
        await Assert
            .That(() => PrivateKeyJwt.CreateAssertion(options, "https://idp.test"))
            .ThrowsExactly<ArgumentException>();

        var pem = new IdentityClientOptions
        {
            ClientId = "worker",
            PrivateKeyPem = first.ExportPkcs8PrivateKeyPem(),
            KeyId = "pem-key",
        };
        await Assert
            .That(
                await IsSignedBy(
                    PrivateKeyJwt.CreateAssertion(pem, "https://idp.test"),
                    first,
                    "pem-key"
                )
            )
            .IsTrue();
        pem.PrivateKeyPem = second.ExportPkcs8PrivateKeyPem();
        await Assert
            .That(
                await IsSignedBy(
                    PrivateKeyJwt.CreateAssertion(pem, "https://idp.test"),
                    second,
                    "pem-key"
                )
            )
            .IsTrue();
    }

    private static async Task<bool> IsSignedBy(string assertion, RSA rsa, string kid)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(
            assertion,
            new TokenValidationParameters
            {
                ValidIssuer = "worker",
                ValidAudience = "https://idp.test",
                IssuerSigningKey = new JsonWebKey(TestKeys.Public(rsa, kid)),
                ValidAlgorithms = ["RS256"],
                ValidTypes = [PrivateKeyJwt.ClientAuthenticationType],
            }
        );
        return result.IsValid;
    }

    private static async Task AssertSafe(Exception error, params string[] privateValues)
    {
        await Assert.That(error.InnerException).IsNull();
        var leaked = privateValues.Any(v => error.ToString().Contains(v, StringComparison.Ordinal));
        await Assert.That(leaked).IsFalse();
    }

    [Test]
    public async Task Assertions_follow_rfc7523bis_unless_the_token_endpoint_profile_is_requested()
    {
        using var rsa = RSA.Create(2048);
        var options = new IdentityClientOptions
        {
            ClientId = "worker",
            PrivateKeyBase64 = TestKeys.Encode(rsa, "profile-key"),
        };
        var issuer = new JsonWebToken(PrivateKeyJwt.CreateAssertion(options, "https://idp.test"));
        await Assert.That(issuer.Typ).IsEqualTo(PrivateKeyJwt.ClientAuthenticationType);
        await Assert.That(issuer.Audiences.ToArray()).IsEquivalentTo(["https://idp.test"]);
        using (var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(issuer.EncodedPayload)))
            await Assert
                .That(payload.RootElement.GetProperty("aud").ValueKind)
                .IsEqualTo(JsonValueKind.String);
        var legacy = new JsonWebToken(
            PrivateKeyJwt.CreateTokenEndpointAssertion(options, "https://idp.test/connect/token")
        );
        await Assert.That(legacy.Typ).IsEqualTo("JWT");
        await Assert
            .That(legacy.Audiences.ToArray())
            .IsEquivalentTo(["https://idp.test/connect/token"]);
    }

    private static async Task ValidateAssertion(IdentityClientOptions options, string publicJwk)
    {
        const string audience = "https://localhost";
        var assertion = PrivateKeyJwt.CreateAssertion(options, audience);
        var jwt = new JsonWebToken(assertion);
        await Assert.That(jwt.Typ).IsEqualTo(PrivateKeyJwt.ClientAuthenticationType);
        await Assert.That(jwt.Alg).IsEqualTo("RS256");
        await Assert.That(jwt.Kid).IsEqualTo(new JsonWebKey(publicJwk).Kid);
        await Assert.That(jwt.Subject).IsEqualTo(options.ClientId);
        await Assert
            .That(jwt.ValidTo - jwt.IssuedAt)
            .IsBetween(TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(61));
        await Assert
            .That(new JsonWebToken(PrivateKeyJwt.CreateAssertion(options, audience)).Id)
            .IsNotEqualTo(jwt.Id);
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
        await Assert.That(result.IsValid).IsTrue();
    }
}
