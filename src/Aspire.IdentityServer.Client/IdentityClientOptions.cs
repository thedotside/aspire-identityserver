namespace Aspire.IdentityServer.Client;

/// <summary>Matches the default WithIdentityClient projection. Bind any configuration section.</summary>
public sealed class IdentityClientOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string AuthenticationMethod { get; set; } = "none";
    public string? ClientSecret { get; set; }
    public string? PrivateKeyPem { get; set; }

    /// <summary>Base64(UTF8(private RSA JWK JSON)), mutually exclusive with PrivateKeyPem.</summary>
    public string? PrivateKeyBase64 { get; set; }
    public string? KeyId { get; set; }
    public string Scopes { get; set; } = "";
    public string? TokenExchangeAudience { get; set; }
}
