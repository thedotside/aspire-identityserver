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
        Func<HttpContent>? fresh = null;
        if (string.Equals(client.AuthenticationMethod, "private_key_jwt", StringComparison.Ordinal))
        {
            var assertionIssuer = issuer ?? client.Authority;
            fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
            fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, assertionIssuer);
            fresh = () =>
                new FormUrlEncodedContent(
                    new Dictionary<string, string>(fields, StringComparer.Ordinal)
                    {
                        ["client_assertion"] = PrivateKeyJwt.CreateAssertion(
                            client,
                            assertionIssuer
                        ),
                    }
                );
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
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        // A retried attempt must not replay the assertion; the token backchannel re-signs it.
        if (fresh is not null)
            request.Options.Set(FreshContentKey, fresh);
        return request;
    }
}
