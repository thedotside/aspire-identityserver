namespace Aspire.IdentityServer.Server;

internal sealed class ServerConfiguration
{
    public string Name { get; set; } = "identityserver";
    public string Issuer { get; set; } = "";
    public bool AcceptTokenEndpointAudience { get; set; }
    public List<ClientConfiguration> Clients { get; set; } = [];
    public List<ApiConfiguration> Apis { get; set; } = [];
    public List<UserConfiguration> Users { get; set; } = [];
}

internal sealed class ClientConfiguration
{
    public string ClientId { get; set; } = "";
    public string GrantType { get; set; } = "";
    public string AuthenticationMethod { get; set; } = "none";

    /// <summary>Base64(SHA-256(secret)), as Duende stores it. The secret itself is never sent.</summary>
    public string? ClientSecretHash { get; set; }
    public string? PublicJwk { get; set; }
    public bool RequirePkce { get; set; } = true;
    public List<string> Scopes { get; set; } = [];
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<TokenExchangeConfiguration> TokenExchanges { get; set; } = [];
}

internal sealed class TokenExchangeConfiguration
{
    public string SubjectClientId { get; set; } = "";
    public string Audience { get; set; } = "";
    public List<string> UserClaims { get; set; } = [];
}

internal sealed class ApiConfiguration
{
    public string Audience { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

internal sealed class UserConfiguration
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}
