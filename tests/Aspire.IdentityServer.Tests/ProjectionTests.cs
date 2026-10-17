using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Hosting;
using Xunit;

namespace Aspire.IdentityServer.Tests;

public sealed class ProjectionTests
{
    [Fact]
    public async Task Private_keys_remain_secret_references_and_do_not_enter_provider_configuration()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        var server = builder.AddIdentityServer("projection-idp");
        server.AddApiResource("orders").WithScopes("read");
        var client = server.AddClientCredentialsClient("worker").WithGeneratedPrivateKeyJwt().WithScopes("read");
        var consumer = builder.AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, "Nested:First")
            .WithIdentityClient(client, p => p.PrivateKeyPem("Nested:Second:Key").Authority("Issuer"));
        var providerEnvironment = await Evaluate(server);
        var consumerEnvironment = await Evaluate(consumer);
        Assert.DoesNotContain(providerEnvironment.Keys, key => key.Contains("PrivateKey", StringComparison.Ordinal));
        Assert.DoesNotContain(providerEnvironment.Values, value => ReferenceEquals(value, client.PrivateKeyPem!.Resource));
        Assert.Same(client.PrivateKeyPem!.Resource, consumerEnvironment["Nested__First__PrivateKeyPem"]);
        Assert.Same(client.PrivateKeyPem.Resource, consumerEnvironment["Nested__Second__Key"]);
        Assert.True(Assert.IsType<ParameterResource>(consumerEnvironment["Nested__First__PrivateKeyPem"]).Secret);
        Assert.IsType<EndpointReference>(consumerEnvironment["Issuer"]);
        Assert.DoesNotContain("PRIVATE KEY", client.PublicJwk!);
    }

    [Fact]
    public async Task Undeclared_scopes_fail_before_starting_the_provider()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        var server = builder.AddIdentityServer("invalid-scopes");
        server.AddClientCredentialsClient("worker").WithGeneratedClientSecret().WithScopes("undeclared");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(server));
        Assert.Contains("declared API scopes", error.Message);
    }

    [Fact]
    public async Task Projecting_missing_credentials_fails_with_the_client_name()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        var server = builder.AddIdentityServer("missing-key");
        var client = server.AddAuthorizationCodeClient("public-client");
        var consumer = builder.AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(client, p => p.PrivateKeyPem("Key"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(consumer));
        Assert.Contains("public-client", error.Message);
    }

    [Fact]
    public async Task Conflicting_projections_do_not_silently_overwrite_credentials()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        var server = builder.AddIdentityServer("conflict");
        var first = server.AddClientCredentialsClient("first").WithGeneratedClientSecret();
        var second = server.AddClientCredentialsClient("second").WithGeneratedClientSecret();
        var consumer = builder.AddExecutable("consumer", "dotnet", ".")
            .WithIdentityClient(first, "OAuth").WithIdentityClient(second, "OAuth");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Evaluate(consumer));
    }

    private static async Task<Dictionary<string, object>> Evaluate<T>(IResourceBuilder<T> resource) where T : IResource
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(resource.ApplicationBuilder.ExecutionContext, resource.Resource, values, default);
        foreach (var callback in resource.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await callback.Callback(context);
        return values;
    }
}
