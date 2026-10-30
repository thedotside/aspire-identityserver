namespace Aspire.IdentityServer;

// Compile the same protocol-claim boundary into Hosting and Runtime, without coupling
// either package to the optional client helper.
internal static class TokenExchangeClaims
{
    private static readonly HashSet<string> ProtocolClaims = new(StringComparer.Ordinal)
    {
        "sub",
        "iss",
        "aud",
        "exp",
        "iat",
        "nbf",
        "jti",
        "client_id",
        "scope",
        "act",
        "may_act",
        "cnf",
        "azp",
        "nonce",
        "at_hash",
        "c_hash",
        "s_hash",
        "sid",
        "auth_time",
        "amr",
        "acr",
        "idp",
        "typ",
        "token_type",
    };

    internal static bool IsUserClaim(string type) =>
        !string.IsNullOrWhiteSpace(type)
        && !type.Any(char.IsWhiteSpace)
        && !type.Any(char.IsControl)
        && !ProtocolClaims.Contains(type);
}
