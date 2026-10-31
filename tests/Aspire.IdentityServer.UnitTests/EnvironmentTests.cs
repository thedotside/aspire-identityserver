using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.IdentityServer.Tests;

public sealed class EnvironmentTests
{
    [Test]
    [Arguments("Development")]
    [Arguments("Testing")]
    public async Task The_provider_inherits_a_development_or_testing_apphost_environment(
        string environment
    )
    {
        var server = TestApp.CreateBuilder(environment).AddIdentityServer("idp");
        var values = await ProjectionTests.Evaluate(server);
        await Assert.That(values["ASPNETCORE_ENVIRONMENT"]).IsEqualTo(environment);
        await Assert
            .That(values[RuntimeContract.EnvironmentVariable])
            .IsEqualTo(
                RuntimeContract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            );
    }

    [Test]
    [Arguments(false, "false")]
    [Arguments(true, "true")]
    public async Task Strict_client_assertion_audiences_are_the_default(bool accept, string value)
    {
        var server = TestApp
            .CreateBuilder()
            .AddIdentityServer("idp", options => options.AcceptTokenEndpointAudience = accept);
        var values = await ProjectionTests.Evaluate(server);
        await Assert.That(values["IdentityServer__AcceptTokenEndpointAudience"]).IsEqualTo(value);
    }

    [Test]
    [Arguments("Production")]
    [Arguments("Staging")]
    [Arguments("Dev")]
    public async Task Other_apphost_environments_are_rejected_before_any_resource_is_added(
        string environment
    )
    {
        var builder = TestApp.CreateBuilder(environment);
        var error = await Assert
            .That(() => builder.AddIdentityServer("idp"))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(error!.Message).Contains($"not '{environment}'");
        await Assert.That(builder.Resources.OfType<IdentityServerResource>()).IsEmpty();
    }
}
