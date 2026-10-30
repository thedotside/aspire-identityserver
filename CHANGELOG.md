# Changelog

## 0.1.0-alpha.2

- Native `Base64(UTF8(private RSA JWK JSON))` credentials for `private_key_jwt`: generated keys, imported values, or existing Aspire secret parameters with a matching public JWK.
- `PrivateKeyBase64` configuration and standard/custom projections; only the public key is registered in the provider.
- RS256 assertions use the JWK's `kid`, reject conflicting `KeyId` or simultaneous PEM/Base64 configuration, and preserve fresh `jti` and short expiry.
- Invalid private input is rejected without retaining input-bearing inner exceptions or sending private JSON to IdentityModel diagnostics. PEM and `client_secret_post` remain supported.
- Same-provider token exchange and an opt-in handler for named HTTP clients.
- BFF, CC Xp/Capa and Kubernetes configuration guide, including the separate application and Kubernetes Base64 encodings.

## 0.1.0-alpha.1

- Initial local-development OAuth/OIDC integration, provider runtime, PEM assertion helper and configuration projections.
