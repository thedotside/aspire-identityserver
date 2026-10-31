using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Tests;

public sealed class AspireConventionTests
{
    [Test]
    public async Task A_provider_is_a_service_discovery_reference()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("idp");
        var consumer = builder.AddExecutable("consumer", "dotnet", ".").WithReference(server);
        var values = await ProjectionTests.Evaluate(consumer);
        await Assert.That(values).ContainsKey("services__idp__https__0");
    }

    [Test]
    public async Task Api_resources_project_the_authority_and_audience_under_both_names()
    {
        var builder = TestApp.CreateBuilder();
        var server = builder.AddIdentityServer("idp");
        var api = server.AddApiResource("orders").WithScopes("orders.read");
        var current = builder
            .AddExecutable("current", "dotnet", ".")
            .WithIdentityApiResource(api, "Authentication:Inbound");
#pragma warning disable CS0618 // The renamed method must keep its behavior.
        var renamed = builder
            .AddExecutable("renamed", "dotnet", ".")
            .WithIdentityResource(api, "Authentication:Inbound");
#pragma warning restore CS0618
        foreach (var consumer in new[] { current, renamed })
        {
            var values = await ProjectionTests.Evaluate(consumer);
            await Assert
                .That(values["Authentication__Inbound__Authority"])
                .IsTypeOf<EndpointReference>();
            await Assert.That(values["Authentication__Inbound__Audience"]).IsEqualTo("orders");
        }
    }
}
