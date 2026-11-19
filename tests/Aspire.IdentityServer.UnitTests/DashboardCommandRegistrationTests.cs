using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.IdentityServer.Hosting;

namespace Aspire.IdentityServer.Tests;

public sealed class DashboardCommandRegistrationTests
{
    [Test]
    public async Task Providers_offer_registration_and_machine_token_commands()
    {
        var builder = TestApp.CreateBuilder();
        var idp = builder.AddIdentityServer("idp");
        idp.AddApiResource("orders").WithScopes("read");
        idp.AddClientCredentialsClient("worker").WithGeneratedClientSecret().WithScopes("read");
        idp.AddClientCredentialsClient("device").WithGeneratedPrivateKeyJwt().WithScopes("read");
        idp.AddAuthorizationCodeClient("web")
            .WithScopes("openid")
            .WithRedirectUri(idp.GetEndpoint("https"), "/callback");
        DashboardCommands.Add(idp);
        var commands = idp
            .Resource.Annotations.OfType<ResourceCommandAnnotation>()
            .ToDictionary(c => c.Name, StringComparer.Ordinal);
        await Assert.That(commands.ContainsKey(DashboardCommands.Registrations)).IsTrue();
        var token = commands[DashboardCommands.RequestToken];
        var client = token.Arguments!.Single();
        await Assert.That(client.InputType).IsEqualTo(InputType.Choice);
        await Assert.That(client.Required).IsTrue();
        await Assert
            .That(client.Options!.Select(o => o.Key).ToArray())
            .IsEquivalentTo(["worker", "device"]);
    }

    [Test]
    public async Task A_provider_without_machine_clients_offers_no_token_command()
    {
        var idp = TestApp.CreateBuilder().AddIdentityServer("idp");
        DashboardCommands.Add(idp);
        var names = idp
            .Resource.Annotations.OfType<ResourceCommandAnnotation>()
            .Select(c => c.Name)
            .ToArray();
        await Assert.That(names).Contains(DashboardCommands.Registrations);
        await Assert.That(names).DoesNotContain(DashboardCommands.RequestToken);
    }
}
