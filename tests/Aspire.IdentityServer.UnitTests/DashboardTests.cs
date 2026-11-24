using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Hosting;

namespace Aspire.IdentityServer.Tests;

public sealed class DashboardTests
{
    [Test]
    public async Task Generated_parameters_are_named_after_their_client_and_grouped_under_the_provider()
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("idp").WithTestUser("alice", "alice");
        var web = idp.AddAuthorizationCodeClient("Web.App").WithGeneratedClientSecret();
        var worker = idp.AddClientCredentialsClient("worker").WithGeneratedBase64PrivateKeyJwt();
        var legacy = idp.AddClientCredentialsClient("urn:legacy").WithGeneratedPrivateKeyJwt();
        // A second client whose name collides after normalization keeps a unique index name.
        var twin = idp.AddAuthorizationCodeClient("web-app").WithGeneratedClientSecret();
        var parameters = new[]
        {
            web.ClientSecret!.Resource,
            worker.PrivateKeyBase64!.Resource,
            legacy.PrivateKeyPem!.Resource,
            twin.ClientSecret!.Resource,
        };
        await Assert
            .That(parameters.Select(p => p.Name).ToArray())
            .IsEquivalentTo([
                "idp-web-app-secret",
                "idp-worker-key",
                "idp-urn-legacy-key",
                "idp-client-3-secret",
            ]);
        var password = builder
            .Resources.OfType<ParameterResource>()
            .Single(p => string.Equals(p.Name, "idp-alice-password", StringComparison.Ordinal));
        foreach (var parameter in parameters.Append(password))
            await Assert
                .That(
                    parameter
                        .Annotations.OfType<ResourceRelationshipAnnotation>()
                        .Any(r =>
                            ReferenceEquals(r.Resource, idp.Resource)
                            && string.Equals(r.Type, "Parent", StringComparison.Ordinal)
                        )
                )
                .IsTrue();
    }

    [Test]
    public async Task Providers_show_an_icon_and_a_discovery_link()
    {
        var idp = TestApp.CreateBuilder().AddIdentityServer("idp").Resource;
        await Assert.That(idp.Annotations.OfType<ResourceIconAnnotation>()).IsNotEmpty();
        await Assert.That(idp.Annotations.OfType<ResourceUrlsCallbackAnnotation>()).IsNotEmpty();
    }

    [Test]
    public async Task Registrations_list_the_simulated_mfa()
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder
            .AddIdentityServer("idp")
            .WithSimulatedMfa("111111", "urn:test:mfa", "urn:test:high");
        var plain = builder.AddIdentityServer("plain");
        await Assert
            .That(await DashboardCommands.MarkdownAsync(idp.Resource, CancellationToken.None))
            .Contains(
                "### Simulated MFA\n\n| Code | acr values |\n| --- | --- |\n| 111111 | urn:test:mfa urn:test:high |\n"
            );
        await Assert
            .That(await DashboardCommands.MarkdownAsync(plain.Resource, CancellationToken.None))
            .DoesNotContain("Simulated MFA");
    }
}
