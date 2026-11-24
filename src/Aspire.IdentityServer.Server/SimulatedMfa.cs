namespace Aspire.IdentityServer.Server;

/// <summary>
/// The simulated second factor declared in the AppHost: which requested level asks for the code,
/// and the page that asks for it.
/// </summary>
internal static class SimulatedMfa
{
    /// <summary>The verification page; Duende appends the authorization request as its return URL.</summary>
    internal const string PagePath = "/Account/Mfa";

    /// <summary>The amr values that a verified code adds (RFC 8176): one-time password, multiple factors.</summary>
    internal static readonly string[] AuthenticationMethods = ["otp", "mfa"];

    /// <summary>The first requested acr value that the provider declares, or <see langword="null" />.</summary>
    internal static string? RequestedLevel(
        IEnumerable<string> acrValues,
        IReadOnlyCollection<string> declared
    ) => acrValues.FirstOrDefault(value => declared.Contains(value, StringComparer.Ordinal));

    internal static bool IsValidCode(string? code) =>
        code is { Length: >= 4 and <= 10 } && code.All(char.IsAsciiDigit);
}
