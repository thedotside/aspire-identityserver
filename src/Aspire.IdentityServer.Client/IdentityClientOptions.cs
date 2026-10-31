namespace Aspire.IdentityServer.Client;

/// <summary>Matches the default WithIdentityClient projection. Bind any configuration section.</summary>
public sealed class IdentityClientOptions
{
    /// <summary>The provider issuer; its discovery document is at <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string Authority { get; set; } = "";

    /// <summary>The registered client identifier, also the <c>iss</c> and <c>sub</c> of its assertions.</summary>
    public string ClientId { get; set; } = "";

    /// <summary><c>none</c>, <c>client_secret_post</c> or <c>private_key_jwt</c>.</summary>
    public string AuthenticationMethod { get; set; } = "none";

    /// <summary>The shared secret of a <c>client_secret_post</c> client.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>A private RSA key in PEM, mutually exclusive with <see cref="PrivateKeyBase64" />.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Base64(UTF8(private RSA JWK JSON)), mutually exclusive with PrivateKeyPem.</summary>
    public string? PrivateKeyBase64 { get; set; }

    /// <summary>The <c>kid</c> of the registered public key; required with PEM, checked against the JWK otherwise.</summary>
    public string? KeyId { get; set; }

    /// <summary>Space-delimited scopes requested by default.</summary>
    public string Scopes { get; set; } = "";

    /// <summary>The default audience of token exchanges, when the AppHost allows a single one.</summary>
    public string? TokenExchangeAudience { get; set; }
}
