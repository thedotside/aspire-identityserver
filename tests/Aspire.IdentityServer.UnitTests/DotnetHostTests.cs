using Aspire.Hosting;
using Aspire.IdentityServer.Hosting;

namespace Aspire.IdentityServer.Tests;

public sealed class DotnetHostTests
{
    private static readonly string Executable = OperatingSystem.IsWindows()
        ? "dotnet.exe"
        : "dotnet";

    [Test]
    public async Task The_host_of_a_dotnet_started_apphost_is_reused()
    {
        var host = Path.Combine(Path.GetTempPath(), "custom", Executable);
        await Assert.That(DotnetHost.Resolve(_ => null, "/nowhere/", host)).IsEqualTo(host);
    }

    [Test]
    public async Task The_muxer_of_the_running_runtime_comes_before_path_and_cli_settings()
    {
        using var root = new TemporaryDirectory();
        var runtime = Directory.CreateDirectory(
            Path.Combine(root.Path, "shared", "Microsoft.NETCore.App", "10.0.0")
        );
        var muxer = Path.Combine(root.Path, Executable);
        await File.WriteAllTextAsync(muxer, "");
        using var other = new TemporaryDirectory();
        var cli = Path.Combine(other.Path, Executable);
        await File.WriteAllTextAsync(cli, "");
        var resolved = DotnetHost.Resolve(
            name => string.Equals(name, "DOTNET_HOST_PATH", StringComparison.Ordinal) ? cli : null,
            runtime.FullName + Path.DirectorySeparatorChar,
            Path.Combine(root.Path, "MyAppHost")
        );
        await Assert.That(resolved).IsEqualTo(muxer);
    }

    [Test]
    public async Task The_cli_host_then_path_are_the_fallbacks()
    {
        using var other = new TemporaryDirectory();
        var cli = Path.Combine(other.Path, Executable);
        await File.WriteAllTextAsync(cli, "");
        await Assert
            .That(DotnetHost.Resolve(_ => cli, other.Path, Path.Combine(other.Path, "AppHost")))
            .IsEqualTo(cli);
        await Assert
            .That(DotnetHost.Resolve(_ => null, other.Path, Path.Combine(other.Path, "AppHost")))
            .IsEqualTo("dotnet");
    }

    [Test]
    public async Task Providers_run_with_an_existing_host_of_this_machine()
    {
        var server = TestApp.CreateBuilder().AddIdentityServer("host-idp").Resource;
        await Assert.That(Path.IsPathRooted(server.Command)).IsTrue();
        await Assert.That(File.Exists(server.Command)).IsTrue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } =
            Directory
                .CreateDirectory(
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"aspire-idp-host-{Guid.NewGuid():N}"
                    )
                )
                .FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
