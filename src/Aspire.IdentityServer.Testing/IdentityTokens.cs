namespace Aspire.Hosting.Testing;

/// <summary>The tokens that a local identity provider issued to a test.</summary>
public sealed class IdentityTokens
{
    /// <summary>The access token, to send as <c>Authorization: Bearer</c>.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The token type returned by the provider, <c>Bearer</c>.</summary>
    public required string TokenType { get; init; }

    /// <summary>The ID token of a user sign-in; absent for machine clients.</summary>
    public string? IdToken { get; init; }

    /// <summary>The refresh token, when the client requested <c>offline_access</c>.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>The granted scopes, space-delimited, when the provider returned them.</summary>
    public string? Scope { get; init; }

    /// <summary>The lifetime of the access token in seconds, when the provider returned it.</summary>
    public int? ExpiresIn { get; init; }
}
