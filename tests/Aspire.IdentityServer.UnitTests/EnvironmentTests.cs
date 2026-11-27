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
    public async Task Client_assertion_audiences_are_strict_by_default()
    {
        await Assert
            .That(new IdentityServerOptions().StrictClientAssertionAudienceValidation)
            .IsTrue();
        var values = await ProjectionTests.Evaluate(
            TestApp.CreateBuilder().AddIdentityServer("idp")
        );
        await Assert.That(values["IdentityServer__AcceptTokenEndpointAudience"]).IsEqualTo("false");
    }

    [Test]
    [Arguments(true, "false")]
    [Arguments(false, "true")]
    public async Task The_strict_audience_option_is_projected(bool strict, string accept)
    {
        var server = TestApp
            .CreateBuilder()
            .AddIdentityServer("idp", options =>
                options.StrictClientAssertionAudienceValidation = strict
            );
        var values = await ProjectionTests.Evaluate(server);
        await Assert.That(values["IdentityServer__AcceptTokenEndpointAudience"]).IsEqualTo(accept);
    }

    [Test]
    public async Task The_obsolete_audience_option_mirrors_the_strict_one()
    {
#pragma warning disable CS0618 // The renamed option stays for existing AppHosts.
        var options = new IdentityServerOptions { AcceptTokenEndpointAudience = true };
        await Assert.That(options.StrictClientAssertionAudienceValidation).IsFalse();
        options.StrictClientAssertionAudienceValidation = true;
        await Assert.That(options.AcceptTokenEndpointAudience).IsFalse();
#pragma warning restore CS0618
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
