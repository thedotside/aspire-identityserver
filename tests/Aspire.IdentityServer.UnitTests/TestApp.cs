using Aspire.Hosting;

namespace Aspire.IdentityServer.Tests;

/// <summary>In-memory application models: nothing is started and no network is used.</summary>
internal static class TestApp
{
    internal static IDistributedApplicationBuilder CreateBuilder(
        string environment = "Development"
    ) =>
        DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                DisableDashboard = true,
                Args = ["--environment", environment],
            }
        );
}
