using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Hosting;
using Xunit;

namespace Aspire.IdentityServer.Tests;

public sealed class ProjectionTests
{
    [Fact]
    public async Task Multiple_exchange_audiences_require_explicit_selection()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
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
        Assert.Null(machine.TokenExchangeAudience);
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(machine, "OAuth");
        var environment = await Evaluate(consumer);
        Assert.DoesNotContain(
            "OAuth__TokenExchangeAudience",
            environment.Keys,
            StringComparer.Ordinal
        );
        var explicitProjection = builder
            .AddExecutable("explicit", "dotnet", ".")
            .WithIdentityClient(machine, p => p.TokenExchangeAudience("Audience"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(explicitProjection));
    }

    [Fact]
    public async Task Token_exchange_projects_policy_and_preserves_deferred_credentials()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
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
        Assert.Equal(
            "web",
            providerEnvironment["IdentityServer__Clients__1__TokenExchanges__0__SubjectClientId"]
        );
        Assert.Equal(
            "orders",
            providerEnvironment["IdentityServer__Clients__1__TokenExchanges__0__Audience"]
        );
        Assert.Equal(
            "name",
            providerEnvironment["IdentityServer__Clients__1__TokenExchanges__0__UserClaims__0"]
        );
        Assert.Equal(
            "client_credentials",
            providerEnvironment["IdentityServer__Clients__1__GrantType"]
        );
        var consumerEnvironment = await Evaluate(consumer);
        Assert.Equal("orders", consumerEnvironment["OAuth__TokenExchangeAudience"]);
        Assert.IsType<EndpointReference>(consumerEnvironment["OAuth__Authority"]);
        Assert.Same(machine.PrivateKeyPem!.Resource, consumerEnvironment["OAuth__PrivateKeyPem"]);
        Assert.DoesNotContain(
            providerEnvironment.Values,
            v => ReferenceEquals(v, machine.PrivateKeyPem.Resource)
        );
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("scope")]
    [InlineData("aud")]
    [InlineData("act")]
    [InlineData("exp")]
    [InlineData("auth_time")]
    [InlineData("")]
    public void Token_exchange_cannot_copy_protocol_claims(string claim)
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("claims-idp");
        var browser = server.AddAuthorizationCodeClient("web");
        var api = server.AddApiResource("orders");
        var machine = server.AddClientCredentialsClient("device");
        Assert.Throws<ArgumentException>(() => machine.WithTokenExchangeFrom(browser, api, claim));
    }

    [Fact]
    public void Token_exchange_rejects_cross_provider_policies_and_wrong_client_types()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var a = builder.AddIdentityServer("a");
        var b = builder.AddIdentityServer("b");
        var browserA = a.AddAuthorizationCodeClient("web");
        var browserB = b.AddAuthorizationCodeClient("web");
        var machine = a.AddClientCredentialsClient("device");
        var apiA = a.AddApiResource("orders");
        var apiB = b.AddApiResource("orders");
        Assert.Throws<ArgumentException>(() => machine.WithTokenExchangeFrom(browserB, apiA));
        Assert.Throws<ArgumentException>(() => machine.WithTokenExchangeFrom(browserA, apiB));
        Assert.Throws<ArgumentException>(() => machine.WithTokenExchangeFrom(machine, apiA));
        Assert.Throws<InvalidOperationException>(() =>
            browserA.WithTokenExchangeFrom(browserA, apiA)
        );
    }

    [Fact]
    public async Task Token_exchange_requires_a_shared_api_scope_before_starting()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(server));
        Assert.Contains("shared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_keys_remain_secret_references_and_do_not_enter_provider_configuration()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
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
        Assert.DoesNotContain(
            providerEnvironment.Keys,
            key => key.Contains("PrivateKey", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(
            providerEnvironment.Values,
            value => ReferenceEquals(value, client.PrivateKeyPem!.Resource)
        );
        Assert.Same(
            client.PrivateKeyPem!.Resource,
            consumerEnvironment["Nested__First__PrivateKeyPem"]
        );
        Assert.Same(client.PrivateKeyPem.Resource, consumerEnvironment["Nested__Second__Key"]);
        Assert.True(
            Assert
                .IsType<ParameterResource>(consumerEnvironment["Nested__First__PrivateKeyPem"])
                .Secret
        );
        Assert.IsType<EndpointReference>(consumerEnvironment["Issuer"]);
        Assert.DoesNotContain("PRIVATE KEY", client.PublicJwk!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undeclared_scopes_fail_before_starting_the_provider()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("invalid-scopes");
        server
            .AddClientCredentialsClient("worker")
            .WithGeneratedClientSecret()
            .WithScopes("undeclared");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(server));
        Assert.Contains("declared API scopes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projecting_missing_credentials_fails_with_the_client_name()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("missing-key");
        var client = server.AddAuthorizationCodeClient("public-client");
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, p => p.PrivateKeyPem("Key"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(consumer));
        Assert.Contains("public-client", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_projections_do_not_silently_overwrite_credentials()
    {
        var builder = DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions { DisableDashboard = true }
        );
        var server = builder.AddIdentityServer("conflict");
        var first = server.AddClientCredentialsClient("first").WithGeneratedClientSecret();
        var second = server.AddClientCredentialsClient("second").WithGeneratedClientSecret();
        var consumer = builder
            .AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(first, "OAuth")
            .WithIdentityClient(second, "OAuth");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(consumer));
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
