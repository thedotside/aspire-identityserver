using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Aspire.IdentityServer.Tests;

/// <summary>The provider's dashboard commands, executed as the dashboard does.</summary>
[ClassDataSource<AppFixture>(Shared = SharedType.PerTestSession)]
public sealed class DashboardCommandTests(AppFixture fixture)
{
    [Test]
    [Arguments("worker")]
    [Arguments("device-secret")]
    public async Task The_dashboard_requests_a_token_for_a_machine_client(string clientId)
    {
        var result = await Execute("request-client-token", clientId);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Format).IsEqualTo(CommandResultFormat.Json);
        await Assert.That(result.Data.DisplayImmediately).IsTrue();
        using var answer = JsonDocument.Parse(result.Data.Value);
        var token = answer.RootElement.GetProperty("access_token").GetString()!;
        await Assert.That(new JsonWebToken(token).GetClaim("client_id").Value).IsEqualTo(clientId);
        await Assert
            .That(answer.RootElement.GetProperty("claims").GetProperty("client_id").GetString())
            .IsEqualTo(clientId);
        using var api = fixture.App.CreateHttpClient("api", "https");
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var orders = await api.GetFromJsonAsync<JsonElement>("/orders/a");
        await Assert.That(orders.GetProperty("client").GetString()).IsEqualTo(clientId);
    }

    [Test]
    public async Task Only_machine_clients_of_the_provider_can_be_chosen()
    {
        // Aspire refuses a value outside the choices before the command runs.
        var result = await Execute("request-client-token", "web");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).IsNotNull();
    }

    [Test]
    public async Task The_dashboard_lists_registrations_without_secrets()
    {
        var result = await Execute("show-registrations", client: null);
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Format).IsEqualTo(CommandResultFormat.Markdown);
        var markdown = result.Data.Value;
        foreach (
            var expected in new[] { "| worker |", "| web |", "orders-api-a", "| alice |", "teams" }
        )
            await Assert.That(markdown).Contains(expected);
        var worker = await fixture.Client("idp-a", "worker");
        var device = await fixture.Client("idp-a", "device-secret");
        await Assert.That(markdown).DoesNotContain(worker.PrivateKeyBase64!);
        await Assert.That(markdown).DoesNotContain(device.ClientSecret!);
    }

    private Task<ExecuteCommandResult> Execute(string command, string? client)
    {
        var provider = fixture.Servers.Single(s =>
            string.Equals(s.Name, "idp-a", StringComparison.Ordinal)
        );
        var commands = fixture.App.Services.GetRequiredService<ResourceCommandService>();
        if (client is null)
            return commands.ExecuteCommandAsync(provider, command, CancellationToken.None);
        return commands.ExecuteCommandAsync(
            provider,
            command,
            new InteractionInputCollection([
                new InteractionInput
                {
                    Name = "client",
                    InputType = InputType.Choice,
                    Value = client,
                },
            ]),
            CancellationToken.None
        );
    }
}
