namespace Aspire.IdentityServer.Server;

/// <summary>
/// The sign-up form declared in the AppHost: the page that prompt=create opens, and the claims
/// that it asks for.
/// </summary>
internal static class SignUpForm
{
    /// <summary>The sign-up page; Duende appends the authorization request as its return URL.</summary>
    internal const string PagePath = "/Account/Create";

    /// <summary>
    /// The rule of <c>WithSignUp</c>: distinct profile claims. Unlike a declared user's claims,
    /// <c>name</c> is accepted: this field gives the account its display name.
    /// </summary>
    internal static bool HasValidClaimTypes(IReadOnlyCollection<string> claimTypes) =>
        claimTypes.All(TokenExchangeClaims.IsUserClaim)
        && claimTypes.Distinct(StringComparer.Ordinal).Count() == claimTypes.Count;
}
