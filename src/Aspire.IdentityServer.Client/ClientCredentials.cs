namespace Aspire.IdentityServer.Client;

/// <summary>Builds a standard OAuth client-credentials request without depending on an IdP engine.</summary>
public static class ClientCredentials
{
    /// <summary>The OAuth 2.0 client-credentials grant type.</summary>
    public const string GrantType = "client_credentials";

    /// <summary>Creates a request with fresh client authentication. The caller owns and sends it.</summary>
    /// <param name="client">The confidential machine client and its credentials.</param>
    /// <param name="tokenEndpoint">The HTTPS <c>token_endpoint</c> of the provider.</param>
    /// <param name="scope">
    /// The requested scopes; defaults to the client's projected scopes, and is omitted when both
    /// are empty, which lets the provider grant every scope allowed to the client.
    /// </param>
    /// <param name="issuer">
    /// The provider's issuer identifier, the audience of a private_key_jwt assertion. Defaults to
    /// the configured authority, which OpenID discovery requires to be identical.
    /// </param>
    public static HttpRequestMessage CreateRequest(
        IdentityClientOptions client,
        string tokenEndpoint,
        string? scope = null,
        string? issuer = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = GrantType,
        };
        var requested = scope ?? client.Scopes;
        if (!string.IsNullOrWhiteSpace(requested))
            fields["scope"] = requested;
        return TokenRequests.Create(client, tokenEndpoint, fields, issuer, "Client credentials");
    }
}
