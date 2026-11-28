using Aspire.Hosting;

namespace Aspire.IdentityServer.Tests;

/// <summary>In-memory application models: nothing is started and no network is used.</summary>
internal static class TestApp
{
    /// <summary>
    /// A builder that watches no configuration file: tests create builders in parallel without
    /// disposing them, and a Linux runner allows 128 inotify instances per user, shared with the
    /// providers that the distributed tests start.
    /// </summary>
    internal static IDistributedApplicationBuilder CreateBuilder(
        string environment = "Development",
        params string[] args
    ) =>
        DistributedApplication.CreateBuilder(
            new DistributedApplicationOptions
            {
                DisableDashboard = true,
                Args =
                [
                    "--environment",
                    environment,
                    "--hostBuilder:reloadConfigOnChange",
                    "false",
                    .. args,
                ],
            }
        );
}
