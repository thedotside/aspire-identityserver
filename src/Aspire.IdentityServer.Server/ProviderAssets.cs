namespace Aspire.IdentityServer.Server;

/// <summary>
/// The stylesheet and icon of the provider pages. Embedded in the assembly, as the runtime
/// folder holds assemblies only, and served from the provider's origin, as the pages' policy
/// refuses inline styles.
/// </summary>
internal static class ProviderAssets
{
    public static string Read(string name)
    {
        using var stream =
            typeof(ProviderAssets).Assembly.GetManifestResourceStream(
                $"Aspire.IdentityServer.Server.Assets.{name}"
            ) ?? throw new InvalidOperationException($"The runtime misses its asset '{name}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
