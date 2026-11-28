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
                // Unit tests use in-memory models and never reload configuration files.
                Args = ["--environment", environment, "--hostBuilder:reloadConfigOnChange=false"],
            }
        );
}
