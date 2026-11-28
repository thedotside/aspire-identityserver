namespace Aspire.IdentityServer;

// Compile the same sign-up rule into Hosting and Runtime: a form field issues its claim as text.
internal static class SignUpClaims
{
    // OpenID Connect Core 5.1 types these standard claims as a boolean, a number or a JSON
    // object, and only the provider can declare an email address or a phone number verified.
    private static readonly HashSet<string> TypedClaims = new(StringComparer.Ordinal)
    {
        "email_verified",
        "phone_number_verified",
        "updated_at",
        "address",
    };

    /// <summary>A standard claim that a sign-up field cannot issue: it is not text.</summary>
    internal static bool IsTypedClaim(string type) => TypedClaims.Contains(type);
}
