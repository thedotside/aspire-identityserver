namespace Microsoft.AspNetCore.Authorization;

/// <summary>Scope requirements on the space-delimited <c>scope</c> claim of access tokens.</summary>
public static class ScopeAuthorizationPolicyBuilderExtensions
{
    /// <summary>
    /// Requires an access token granting at least one of <paramref name="scopes" />. The local
    /// provider, like Keycloak, emits <c>scope</c> as one space-delimited string (RFC 9068), which
    /// <c>RequireClaim</c> cannot match token by token.
    /// </summary>
    public static AuthorizationPolicyBuilder RequireScope(
        this AuthorizationPolicyBuilder builder,
        params string[] scopes
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Length == 0 || scopes.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Require at least one non-empty scope.", nameof(scopes));
        var required = scopes.ToHashSet(StringComparer.Ordinal);
        return builder.RequireAssertion(context =>
            context
                .User.FindAll("scope")
                .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Any(required.Contains)
        );
    }
}
