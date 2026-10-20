namespace Aspire.IdentityServer.Client;

/// <summary>Builds a standard OAuth token-exchange request without depending on an IdP engine.</summary>
public static class TokenExchange
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";
    public const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>An optional per-request subject token for a configured named HttpClient.</summary>
    public static readonly HttpRequestOptionsKey<string> SubjectTokenKey = new(
        "TheDotSide.IdentityServer.SubjectToken"
    );

    /// <summary>Creates a request with fresh client authentication. The caller owns and sends it.</summary>
    public static HttpRequestMessage CreateRequest(
        IdentityClientOptions client,
        string tokenEndpoint,
        string subjectToken,
        string? audience = null,
        string? scope = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        audience ??= client.TokenExchangeAudience;
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        if (
            !Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, "https", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
        )
            throw new ArgumentException(
                "Use a discovered HTTPS token endpoint.",
                nameof(tokenEndpoint)
            );
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = GrantType,
            ["client_id"] = client.ClientId,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = AccessTokenType,
            ["requested_token_type"] = AccessTokenType,
            ["audience"] = audience,
            ["scope"] = scope ?? client.Scopes,
        };
        if (string.Equals(client.AuthenticationMethod, "private_key_jwt", StringComparison.Ordinal))
        {
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, tokenEndpoint);
        }
        else if (
            string.Equals(
                client.AuthenticationMethod,
                "client_secret_post",
                StringComparison.Ordinal
            )
        )
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientSecret, nameof(client));
            fields["client_secret"] = client.ClientSecret;
        }
        else
            throw new ArgumentException(
                "Token exchange requires a confidential client.",
                nameof(client)
            );
        return new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
    }
}
