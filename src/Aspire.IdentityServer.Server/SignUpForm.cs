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
    /// The rule of <c>WithSignUp</c>: distinct profile claims that a field issues as text. Unlike
    /// a declared user's claims, <c>name</c> is accepted: this field gives the account its
    /// display name.
    /// </summary>
    internal static bool HasValidClaimTypes(IReadOnlyCollection<string> claimTypes) =>
        claimTypes.All(type =>
            TokenExchangeClaims.IsUserClaim(type) && !SignUpClaims.IsTypedClaim(type)
        )
        && claimTypes.Distinct(StringComparer.Ordinal).Count() == claimTypes.Count;

    /// <summary>
    /// The autocomplete token of a standard claim's field, so that browsers can fill it, or
    /// <see langword="null" /> for a claim that they do not know.
    /// </summary>
    internal static string? Autocomplete(string claimType) =>
        claimType switch
        {
            "email" => "email",
            "given_name" => "given-name",
            "family_name" => "family-name",
            "name" => "name",
            "phone_number" => "tel",
            "birthdate" => "bday",
            _ => null,
        };
}
