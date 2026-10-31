using System.Runtime.InteropServices;

namespace Aspire.IdentityServer.Hosting;

/// <summary>
/// Locates the dotnet host of the runtime that runs the AppHost, instead of the first one on
/// PATH: a workstation can have several .NET installations.
/// </summary>
internal static class DotnetHost
{
    private static readonly Lazy<string> Current = new(() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            RuntimeEnvironment.GetRuntimeDirectory(),
            Environment.ProcessPath
        )
    );

    internal static string Path => Current.Value;

    internal static string Resolve(
        Func<string, string?> environment,
        string runtimeDirectory,
        string? processPath
    )
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        // An AppHost started as `dotnet AppHost.dll` already runs inside the host.
        if (
            processPath is not null
            && string.Equals(
                System.IO.Path.GetFileName(processPath),
                executable,
                StringComparison.OrdinalIgnoreCase
            )
        )
            return processPath;
        // <root>/shared/Microsoft.NETCore.App/<version>/ belongs to <root>/dotnet.
        var root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(runtimeDirectory, "..", "..", "..")
        );
        var muxer = System.IO.Path.Combine(root, executable);
        if (File.Exists(muxer))
            return muxer;
        // Set by the .NET CLI for the processes it starts.
        if (environment("DOTNET_HOST_PATH") is { Length: > 0 } hostPath && File.Exists(hostPath))
            return hostPath;
        return "dotnet";
    }
}
