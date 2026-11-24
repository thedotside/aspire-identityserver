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

    [Test]
    public async Task The_simulated_mfa_reaches_the_runtime()
    {
        var idp = TestApp
            .CreateBuilder()
            .AddIdentityServer("idp")
            .WithSimulatedMfa("111111", "urn:test:mfa", "urn:test:high");
        var values = await ProjectionTests.Evaluate(idp);
        await Assert.That(values["IdentityServer__SimulatedMfa__Code"]).IsEqualTo("111111");
        await Assert
            .That(values["IdentityServer__SimulatedMfa__AcrValues__0"])
            .IsEqualTo("urn:test:mfa");
        await Assert
            .That(values["IdentityServer__SimulatedMfa__AcrValues__1"])
            .IsEqualTo("urn:test:high");
    }

    [Test]
    public async Task A_provider_without_simulated_mfa_writes_no_mfa_variable()
    {
        var values = await ProjectionTests.Evaluate(
            TestApp.CreateBuilder().AddIdentityServer("idp")
        );
        await Assert
            .That(
                values.Keys.Any(k =>
                    k.StartsWith("IdentityServer__SimulatedMfa", StringComparison.Ordinal)
                )
            )
            .IsFalse();
    }

    [Test]
    public async Task The_model_exposes_the_simulated_mfa()
    {
        var idp = TestApp
            .CreateBuilder()
            .AddIdentityServer("idp")
            .WithSimulatedMfa("111111", "urn:test:mfa", "urn:test:high");
        await Assert.That(idp.Resource.SimulatedMfa!.Code).IsEqualTo("111111");
        await Assert
            .That(string.Join(' ', idp.Resource.SimulatedMfa.AcrValues))
            .IsEqualTo("urn:test:mfa urn:test:high");
    }
}
