namespace Aspire.IdentityServer.Client;

/// <summary>Builds a standard OAuth token-exchange request without depending on an IdP engine.</summary>
public static class TokenExchange
{
    /// <summary>The RFC 8693 token-exchange grant type.</summary>
    public const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>The RFC 8693 token type of an access token, for subject and issued tokens.</summary>
    public const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>Rebuilds a token request with a newly signed assertion for a retried attempt.</summary>
    internal static readonly HttpRequestOptionsKey<Func<HttpContent>> FreshContentKey = new(
        "TheDotSide.IdentityServer.FreshClientAssertion"
    );

    /// <summary>An optional per-request subject token for a configured named HttpClient.</summary>
    public static readonly HttpRequestOptionsKey<string> SubjectTokenKey = new(
        "TheDotSide.IdentityServer.SubjectToken"
    );

    /// <summary>Creates a request with fresh client authentication. The caller owns and sends it.</summary>
    /// <param name="client">The confidential machine client and its credentials.</param>
    /// <param name="tokenEndpoint">The HTTPS <c>token_endpoint</c> of the provider.</param>
    /// <param name="subjectToken">The user access token to exchange.</param>
    /// <param name="audience">The target API; defaults to the client's projected exchange audience.</param>
    /// <param name="scope">The requested scopes; defaults to the client's projected scopes.</param>
    /// <param name="issuer">
    /// The provider's issuer identifier, the audience of a private_key_jwt assertion. Defaults to
    /// the configured authority, which OpenID discovery requires to be identical.
    /// </param>
    public static HttpRequestMessage CreateRequest(
        IdentityClientOptions client,
        string tokenEndpoint,
        string subjectToken,
        string? audience = null,
        string? scope = null,
        string? issuer = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        audience ??= client.TokenExchangeAudience;
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = GrantType,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = AccessTokenType,
            ["requested_token_type"] = AccessTokenType,
            ["audience"] = audience,
            ["scope"] = scope ?? client.Scopes,
        };
        return TokenRequests.Create(client, tokenEndpoint, fields, issuer, "Token exchange");
    }
}
