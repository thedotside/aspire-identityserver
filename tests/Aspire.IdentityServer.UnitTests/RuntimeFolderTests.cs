namespace Aspire.IdentityServer.Tests;

/// <summary>The provider folder built next to this test project, as for any consumer.</summary>
public sealed class RuntimeFolderTests
{
    private static readonly string Folder = Path.Combine(
        AppContext.BaseDirectory,
        "identityserver"
    );

    [Test]
    public async Task The_provider_folder_holds_only_the_runtime_closure_without_a_deps_file()
    {
        var files = Directory
            .EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/'))
            .ToArray();
        await Assert.That(files).Contains("Aspire.IdentityServer.Server.dll");
        await Assert.That(files).Contains("Aspire.IdentityServer.Server.runtimeconfig.json");
        await Assert.That(files).Contains("Duende.IdentityServer.dll");
        await Assert
            .That(files)
            .DoesNotContain(f => f.EndsWith(".deps.json", StringComparison.Ordinal));
        // Nothing of the consumer: its assemblies, Aspire, the test framework or the client helper.
        foreach (
            var foreign in new[]
            {
                "Aspire.Hosting",
                "TUnit",
                "Aspire.IdentityServer.Client",
                "Aspire.IdentityServer.UnitTests",
            }
        )
            await Assert
                .That(files)
                .DoesNotContain(f => f.StartsWith(foreign + ".", StringComparison.Ordinal));
        await Assert
            .That(files)
            .DoesNotContain(f => f.StartsWith("runtimes/", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_runtime_rolls_forward_to_a_later_major_dotnet()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(Folder, "Aspire.IdentityServer.Server.runtimeconfig.json")
            )
        );
        var options = document.RootElement.GetProperty("runtimeOptions");
        await Assert.That(options.GetProperty("rollForward").GetString()).IsEqualTo("Major");
    }
}
