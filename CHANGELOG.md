# Changelog

## 0.1.0-alpha.3 - 2026-10-31

Hardening and engineering pass following the October 2026 audit.

### Breaking changes

- Hosting extensions moved to the `Aspire.Hosting` namespace and resource types to `Aspire.Hosting.ApplicationModel`: AppHosts no longer need `using Aspire.IdentityServer.Hosting;`.
- `WithIdentityApiResource` replaces `WithIdentityResource`, kept as an obsolete alias.
- `PrivateKeyJwt.CreateAssertion(client, issuer)` emits the draft-ietf-oauth-rfc7523bis profile (`aud` = issuer, `typ: client-authentication+jwt`); `CreateTokenEndpointAssertion` keeps the original profile. The provider refuses token endpoint audiences unless `IdentityServerOptions.AcceptTokenEndpointAudience` is set.
- `AddIdentityServer` refuses an AppHost outside `Development` or `Testing` and passes the real environment to the provider.
- Hosting and Runtime share configuration contract 2, checked at startup: use the same package versions.

### Security

- The provider listens on loopback in code and verifies its effective addresses.
- Token exchange refuses sender-constrained (`cnf`) subject tokens, returns `invalid_target` for unauthorized audiences and keeps the source `amr` and `idp`.
- `scope` is emitted as a space-delimited string; only RS256 client assertions are accepted.
- Secure, `SameSite=Lax` cookies; CSP `frame-ancestors 'none'` and `Referrer-Policy: no-referrer` on sign-in pages.
- The provider receives only the hash of client secrets.
- The token-exchange handler caches discovery, bounds its backchannel, signs a new assertion for each retried attempt and can restrict API origins.
- Private keys are imported once per options instance and their decoded components wiped.

### Packaging and Aspire integration

- The provider folder holds only the Runtime closure, without the consumer's `deps.json`, and rolls forward to a later major .NET.
- The provider starts with the dotnet host of the AppHost runtime.
- `IdentityServerResource` supports `WithReference`; registration rules run before any resource starts.
- OpenTelemetry export to the Aspire dashboard; provider icon, discovery link and named, grouped generated secrets.
- The redundant MessagePack reference is removed.

### Tooling

- TUnit on Microsoft Testing Platform, with separate unit and distributed test projects; Playwright traces kept on failure.
- CI on Windows and Linux with locked restores and caches; actions pinned by SHA; publishing from the `nuget` environment.
- Changelog, version and commit message checks in CI.

## 0.1.0-alpha.2

- Native `Base64(UTF8(private RSA JWK JSON))` credentials for `private_key_jwt`: generated keys, imported values, or existing Aspire secret parameters with a matching public JWK.
- `PrivateKeyBase64` configuration and standard/custom projections; only the public key is registered in the provider.
- RS256 assertions use the JWK's `kid`, reject conflicting `KeyId` or simultaneous PEM/Base64 configuration, and preserve fresh `jti` and short expiry.
- Invalid private input is rejected without retaining input-bearing inner exceptions or sending private JSON to IdentityModel diagnostics. PEM and `client_secret_post` remain supported.
- Same-provider token exchange and an opt-in handler for named HTTP clients.
- BFF, CC Xp/Capa and Kubernetes configuration guide, including the separate application and Kubernetes Base64 encodings.

## 0.1.0-alpha.1

- Initial local-development OAuth/OIDC integration, provider runtime, PEM assertion helper and configuration projections.
