namespace Aspire.IdentityServer;

// The configuration contract between Hosting and Runtime, compiled into both. Increment it with
// any change of the IdentityServer__* environment written by the AppHost: a mismatched pair of
// packages then fails at start instead of misreading its configuration.
internal static class RuntimeContract
{
    internal const int Version = 4;
    internal const string EnvironmentVariable = "IdentityServer__ContractVersion";
}
