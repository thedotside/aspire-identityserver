# Changelog

## 11.0.0 - Unreleased

- Add explicit `net10.0` and `net11.0` assets to Hosting, Runtime and Client in the same NuGet packages. Preserve the public API and Hosting/Runtime contract 3.
- Validate public package API compatibility against stable 10.0.0, including the retained .NET 10 assets.
- Build the repository with SDK 11.0.100 and run the protocol/browser suite against both frameworks. Keep SDK 10 consumers supported and select a matching provider runtime configuration, including when both runtimes are installed.
- Use ASP.NET Core authentication 10.0.15 for .NET 10 and 11.0.0 for .NET 11; align the Client's Microsoft.Extensions dependencies per framework.
- Verify actual NuGet archives in independent AppHosts and distributed-test consumers built with SDK 10/.NET 10 and SDK 11/.NET 11, with isolated caches.
- Exercise OIDC `max_age` with private-key, secret and public clients without disabling the .NET 11 signed `auth_time` validation.
- Keep Duende 8.0.10 and Aspire 13.6.5. Document the distinction between this integration's .NET 11 tests and Duende's published .NET 10 support policy.

## 10.0.0 - 2026-11-11

- First stable release for .NET 10, promoting the functionality of 0.1.0-alpha.5 without changing OAuth/OIDC behavior or the public API.
- Use the .NET major as a release-family reference; document the supported frameworks explicitly. The planned 11.0.0 family will retain .NET 10 compatibility while validating .NET 11.
- Keep Hosting, Runtime and Client package versions aligned at 10.0.0. The Hosting/Runtime configuration contract remains 3 and Aspire 13.6.5 remains the minimum.
- Validate package API compatibility against 0.1.0-alpha.5 and document stable API versioning and migration from the prereleases.
- The stable status applies to local development and integration testing; the provider remains in-memory, loopback-only and unavailable in production or AppHost publication.

## 0.1.0-alpha.5 - 2026-11-11

### Security

- Require Aspire 13.6.5 or later, including the published Hosting package's dependency floor, to pick up the fix for [GHSA-f7vh-h5g3-7jrh](https://github.com/advisories/GHSA-f7vh-h5g3-7jrh).

### Features

- Requested identity scopes now select user claims in Authorization Code access tokens as well as ID tokens and UserInfo. `WithIdentityScope("communities", "member_of")` is sufficient to select existing memberships when the BFF requests `communities`; the scope remains unavailable to API resources and machine clients.
- Document BFF/YARP direct relay to Xp, and delegated token exchange to Capa when Xp is absent. Token exchange keeps its explicit claim allow-list; the Hosting/Runtime configuration contract remains 3.

### Dependencies and tooling

- Update ASP.NET Core authentication packages to 10.0.15, TUnit to 1.80.0 and the private development analyzer Meziantou.Analyzer to 3.0.325.
- Update the maintenance workflow to Renovate 44.232.1, Node.js 26, actions/checkout v7 and actions/setup-node v7.
- Install Playwright's pinned Chromium headless shell using the runtime libraries already present on hosted Ubuntu runners, avoiding an unnecessary apt mirror dependency while retaining the full browser tests.

## 0.1.0-alpha.4 - 2026-11-04

### Features

- `AddTestUser` returns an `IdentityTestUser` whose `WithClaim(type, values...)` adds profile claims, such as `email`, `given_name` or a multi-valued `team`. A claim reaches a client through a requested identity scope that lists it, or a token exchange that selects it.
- `WithIdentityScope(name, userClaims...)` declares an identity scope beyond `openid`, `profile` and `email`, for example `teams` issuing `team`.
- Interactive clients can request `offline_access`: the provider issues refresh tokens, and a renewed access token can still be exchanged.
- `WithTokenExchangeFrom(subjectClient, api, TokenExchangeScopes.FromMachineClient, userClaims...)` lets the machine client choose, for each user, API scopes the user's token does not carry, without requiring the API audience in that token. The scope must be requested explicitly. `FromSubjectToken` remains the default narrowing rule.

### Breaking changes

- Hosting and Runtime share configuration contract 3: use the same package versions.

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
