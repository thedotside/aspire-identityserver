using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Tests;

public sealed class ProjectionTests
{
    [Test]
    public async Task Multiple_exchange_audiences_require_explicit_selection()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("multiple-targets");
        var first = server.AddApiResource("first").WithScopes("first.read");
        var second = server.AddApiResource("second").WithScopes("second.read");
        var browser = server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid", "first.read", "second.read")
            .WithRedirectUri(server.GetEndpoint("https"), "/callback");
        var machine = server
            .AddClientCredentialsClient("device")
            .WithGeneratedClientSecret()
            .WithScopes("first.read", "second.read")
            .WithTokenExchangeFrom(browser, first)
            .WithTokenExchangeFrom(browser, second);
        await Assert.That(machine.TokenExchangeAudience).IsNull();
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(machine, "OAuth");
        var environment = await Evaluate(consumer);
        await Assert.That(environment).DoesNotContainKey("OAuth__TokenExchangeAudience");
        var explicitProjection = builder
            .AddExecutable("explicit", "dotnet", ".")
            .WithIdentityClient(machine, p => p.TokenExchangeAudience("Audience"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Evaluate(explicitProjection)
        );
    }

    [Test]
    public async Task Token_exchange_projects_policy_and_preserves_deferred_credentials()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("exchange-idp");
        var api = server.AddApiResource("orders").WithScopes("read");
        var browser = server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid", "read")
            .WithRedirectUri(server.GetEndpoint("https"), "/callback");
        var machine = server
            .AddClientCredentialsClient("device")
            .WithGeneratedPrivateKeyJwt()
            .WithScopes("read")
            .WithTokenExchangeFrom(browser, api, "name");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(machine, "OAuth");
        var providerEnvironment = await Evaluate(server);
        await Assert
            .That(
                providerEnvironment[
                    "IdentityServer__Clients__1__TokenExchanges__0__SubjectClientId"
                ]
            )
            .IsEqualTo("web");
        await Assert
            .That(providerEnvironment["IdentityServer__Clients__1__TokenExchanges__0__Audience"])
            .IsEqualTo("orders");
        await Assert
            .That(
                providerEnvironment["IdentityServer__Clients__1__TokenExchanges__0__UserClaims__0"]
            )
            .IsEqualTo("name");
        await Assert
            .That(providerEnvironment["IdentityServer__Clients__1__GrantType"])
            .IsEqualTo("client_credentials");
        var consumerEnvironment = await Evaluate(consumer);
        await Assert.That(consumerEnvironment["OAuth__TokenExchangeAudience"]).IsEqualTo("orders");
        await Assert.That(consumerEnvironment["OAuth__Authority"]).IsTypeOf<EndpointReference>();
        await Assert
            .That(consumerEnvironment["OAuth__PrivateKeyPem"])
            .IsSameReferenceAs(machine.PrivateKeyPem!.Resource);
        await Assert
            .That(providerEnvironment.Values)
            .DoesNotContain(v => ReferenceEquals(v, machine.PrivateKeyPem.Resource));
    }

    [Test]
    [Arguments("sub")]
    [Arguments("scope")]
    [Arguments("aud")]
    [Arguments("act")]
    [Arguments("exp")]
    [Arguments("auth_time")]
    [Arguments("")]
    public async Task Token_exchange_cannot_copy_protocol_claims(string claim)
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("claims-idp");
        var browser = server.AddAuthorizationCodeClient("web");
        var api = server.AddApiResource("orders");
        var machine = server.AddClientCredentialsClient("device");
        await Assert
            .That(() => machine.WithTokenExchangeFrom(browser, api, claim))
            .ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Token_exchange_keeps_the_target_api_local_and_refuses_wrong_client_types()
    {
        var builder = TestApp.CreateBuilder();
        var a = builder.AddIdentityServer("a");
        var b = builder.AddIdentityServer("b");
        var browserA = a.AddAuthorizationCodeClient("web");
        var machine = a.AddClientCredentialsClient("device");
        var apiA = a.AddApiResource("orders");
        var apiB = b.AddApiResource("orders");
        await Assert
            .That(() => machine.WithTokenExchangeFrom(browserA, apiB))
            .ThrowsExactly<ArgumentException>();
        await Assert
            .That(() => machine.WithTokenExchangeFrom(machine, apiA))
            .ThrowsExactly<ArgumentException>();
        await Assert
            .That(() => browserA.WithTokenExchangeFrom(browserA, apiA))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task A_cross_provider_policy_declares_the_trusted_issuer_to_the_target_provider()
    {
        var builder = TestApp.CreateBuilder();
        var a = builder.AddIdentityServer("a");
        var b = builder.AddIdentityServer("b");
        var orders = a.AddApiResource("orders").WithScopes("read");
        var browserB = b.AddAuthorizationCodeClient("web")
            .WithScopes("openid", "read")
            .WithRedirectUri(b.GetEndpoint("https"), "/callback");
        b.AddApiResource("orders-b").WithScopes("read");
        var partner = a.AddClientCredentialsClient("partner")
            .WithGeneratedClientSecret()
            .WithScopes("read")
            .WithTokenExchangeFrom(browserB, orders, "name");
        var values = await Evaluate(a);
        var prefix = "IdentityServer__Clients__0__TokenExchanges__0";
        await Assert.That(values[$"{prefix}__SubjectClientId"]).IsEqualTo("web");
        await Assert.That(values[$"{prefix}__SubjectProvider"]).IsEqualTo("b");
        var issuer = (EndpointReference)values[$"{prefix}__SubjectIssuer"];
        await Assert.That(issuer.Resource.Name).IsEqualTo("b");
        // The trust is directional: provider B receives no policy of provider A.
        var valuesB = await Evaluate(b);
        await Assert
            .That(valuesB.Keys.Any(k => k.Contains("TokenExchanges", StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(partner.TokenExchangeAudience).IsEqualTo("orders");
    }

    [Test]
    public async Task Token_exchange_requires_a_shared_api_scope_before_starting()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("no-shared-scope");
        var api = server.AddApiResource("orders").WithScopes("read", "write");
        var browser = server
            .AddAuthorizationCodeClient("web")
            .WithScopes("openid", "read")
            .WithRedirectUri(server.GetEndpoint("https"), "/callback");
        server
            .AddClientCredentialsClient("device")
            .WithGeneratedClientSecret()
            .WithScopes("write")
            .WithTokenExchangeFrom(browser, api);
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Evaluate(server)
        );
        await Assert.That(error!.Message).Contains("shared");
    }

    [Test]
    public async Task Private_keys_remain_secret_references_and_do_not_enter_provider_configuration()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("projection-idp");
        server.AddApiResource("orders").WithScopes("read");
        var client = server
            .AddClientCredentialsClient("worker")
            .WithGeneratedPrivateKeyJwt()
            .WithScopes("read");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, "Nested:First")
            .WithIdentityClient(
                client,
                p => p.PrivateKeyPem("Nested:Second:Key").Authority("Issuer")
            );
        var providerEnvironment = await Evaluate(server);
        var consumerEnvironment = await Evaluate(consumer);
        await Assert
            .That(providerEnvironment.Keys)
            .DoesNotContain(key => key.Contains("PrivateKey", StringComparison.Ordinal));
        await Assert
            .That(providerEnvironment.Values)
            .DoesNotContain(value => ReferenceEquals(value, client.PrivateKeyPem!.Resource));
        await Assert
            .That(consumerEnvironment["Nested__First__PrivateKeyPem"])
            .IsSameReferenceAs(client.PrivateKeyPem!.Resource);
        await Assert
            .That(consumerEnvironment["Nested__Second__Key"])
            .IsSameReferenceAs(client.PrivateKeyPem.Resource);
        var projected = await Assert
            .That(consumerEnvironment["Nested__First__PrivateKeyPem"])
            .IsTypeOf<ParameterResource>();
        await Assert.That(projected!.Secret).IsTrue();
        await Assert.That(consumerEnvironment["Issuer"]).IsTypeOf<EndpointReference>();
        await Assert.That(client.PublicJwk!).DoesNotContain("PRIVATE KEY");
    }

    [Test]
    public async Task The_provider_receives_only_the_hash_of_a_client_secret()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("hash-idp");
        server.AddApiResource("orders").WithScopes("read");
        var client = server
            .AddClientCredentialsClient("worker")
            .WithGeneratedClientSecret()
            .WithScopes("read");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, "OAuth");
        var provider = await Evaluate(server);
        var secret = client.ClientSecret!.Resource;
        await Assert.That(provider).DoesNotContainKey("IdentityServer__Clients__0__ClientSecret");
        await Assert.That(provider.Values).DoesNotContain(value => ReferenceEquals(value, secret));
        var hash = await Assert
            .That(provider["IdentityServer__Clients__0__ClientSecretHash"])
            .IsAssignableTo<IValueProvider>();
        var value = (await secret.GetValueAsync(default))!;
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))
        );
        var matches = string.Equals(
            await hash!.GetValueAsync(default),
            expected,
            StringComparison.Ordinal
        );
        await Assert.That(matches).IsTrue(); // Never print a secret or its hash on failure.
        await Assert
            .That((await Evaluate(consumer))["OAuth__ClientSecret"])
            .IsSameReferenceAs(secret);
    }

    [Test]
    public async Task Undeclared_scopes_fail_before_starting_the_provider()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("invalid-scopes");
        server
            .AddClientCredentialsClient("worker")
            .WithGeneratedClientSecret()
            .WithScopes("undeclared");
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Evaluate(server)
        );
        await Assert.That(error!.Message).Contains("declared API scopes");
    }

    [Test]
    public async Task Projecting_missing_credentials_fails_with_the_client_name()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("missing-key");
        var client = server.AddAuthorizationCodeClient("public-client");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, p => p.PrivateKeyPem("Key"));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Evaluate(consumer)
        );
        await Assert.That(error!.Message).Contains("public-client");
    }

    [Test]
    public async Task Conflicting_projections_do_not_silently_overwrite_credentials()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("conflict");
        var first = server.AddClientCredentialsClient("first").WithGeneratedClientSecret();
        var second = server.AddClientCredentialsClient("second").WithGeneratedClientSecret();
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(first, "OAuth")
            .WithIdentityClient(second, "OAuth");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Evaluate(consumer));
    }

    internal static async Task<Dictionary<string, object>> Evaluate<T>(IResourceBuilder<T> resource)
        where T : IResource
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(
            resource.ApplicationBuilder.ExecutionContext,
            resource.Resource,
            values,
            default
        );
        foreach (
            var callback in resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()
        )
            await callback.Callback(context);
        return values;
    }
}
