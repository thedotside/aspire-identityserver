# Changelog

## 11.4.0 - Unreleased

- The web sample returns a cancelled sign-in (`access_denied`) to its home page with `AccessDeniedPath = "/"`, and the integration guide shows how. `AddIdentityServerOpenIdConnect` leaves this path to the application: a fixed path would start the sign-in again on a page that requires it, for example under a `FallbackPolicy`.
- The simulated MFA page draws one box per digit of the declared code, without script. Pasting the code and one-time-code autofill keep working. The field takes a paste whole: a code with an extra digit is refused instead of being cut to its length, and spaces around the code are ignored.
- `IdentityServerOptions.StrictClientAssertionAudienceValidation` (default `true`, named after the Duende option it sets) replaces `AcceptTokenEndpointAudience`, which keeps working as an obsolete alias with the inverse meaning.
- Update the maintenance workflow to Renovate 44.260.3.
- `WithSignUp(claimTypes)` declares a sign-up form on a provider: the profile claims it asks for besides the username and password, in order. The **Show registrations** dashboard command lists its fields. Claims that OpenID Connect types as a boolean, a number or an object, such as `email_verified`, are refused at declaration and at the provider's start: a field issues text, and only the provider can verify an address.
- A provider with a sign-up form answers `prompt=create` (OpenID Connect Prompt Create 1.0): discovery lists `create` in `prompt_values_supported`, and the request opens a page that creates the account in the provider's memory, signs it in and resumes the request, simulated MFA included. The account gets a random subject of 32 hexadecimal characters: a restart erases the account, and a subject is never reassigned. Usernames are unique ignoring case, as the sign-in page looks them up, and cannot start or end with a space. **Cancel** returns `access_denied`. A request without the `openid` scope gets `invalid_request` before the page: only an ID token confirms the account. Until the provider restarts, created accounts sign in again, refresh their tokens and exchange them. Providers without a form keep refusing `prompt=create` with `invalid_request`. The web sample adds a **Create an account** link.
- `GetSignUpTokensAsync` of the Testing package creates an account through the sign-up page without a browser and returns its tokens, typing the simulated MFA code when asked. It requires the `openid` scope and fails without an ID token. When the provider refuses its sign-in form, `GetUserTokensAsync` now reports the page's message too.

### Breaking changes

- Hosting and Runtime share configuration contract 6 (sign-up): use the same package versions.

## 11.3.0 - 2026-11-24

- `WithSimulatedMfa(code, acrValues)` declares a simulated second factor on a provider: a fixed development code and the acr values that ask for it. The **Show registrations** dashboard command lists them.
- A provider with a simulated MFA asks for its code when an authorization request asks for a declared level in `acr_values`: after the password, or straight away for a session without that level. The tokens then carry the level as `acr`, with `pwd`, `otp` and `mfa` as `amr`, and the session keeps it until sign-out. `prompt=none` receives `interaction_required`, Cancel `access_denied`, and discovery lists the levels in `acr_values_supported`. The `mfa` scheme of the web sample demonstrates it.
- `GetUserTokensAsync` of the Testing package accepts `acrValues`: it sends them as `acr_values` and types the simulated MFA code declared in the AppHost. Refreshed tokens keep `acr` and `amr`.
- Token exchange copies the `acr` of the source token like its `amr`, from the same provider or from a trusted one.

### Breaking changes

- Hosting and Runtime share configuration contract 5 (simulated MFA): use the same package versions.

## 11.2.0 - 2026-11-23

- Migrate the sample AppHost to `Microsoft.NET.Sdk` and `AddDotnetProject`, with the same target framework for the AppHost and its .NET resources.
- Validate the five packages against the published 11.1.0 API baseline.

### Breaking changes

- Raise the Hosting dependency floor to Aspire 17.0.0. Update Aspire packages in consuming AppHosts and distributed test projects together. .NET 10 and .NET 11 remain supported.

## 11.1.0 - 2026-11-20

- Validate public package API compatibility against stable 11.0.0 for both .NET 10 and .NET 11.
- `WithIdentityClient(client, section)` chooses the projected fields when the consumer starts: credentials and token-exchange policies declared after the projection are no longer silently left out.
- Renovate keeps the SDK 10 installed by the pipelines and the `net10.0` Microsoft.Extensions references in their family, and asks for approval before raising OpenTelemetry. Those versions reach every AppHost: Aspire.Hosting 13.6.5 requires 1.17.0, and the provider currently resolves 1.20.1.
- The sign-in and sign-out pages behave like production providers: `login_hint` pre-fills the username, **Cancel** returns `access_denied` to the client, and a sign-out request carrying a valid `id_token_hint` ends the session without confirmation and returns to the client's `post_logout_redirect_uri`, after any notification frame has loaded. Other sign-out requests still ask for confirmation. The Runtime's Razor page models, public only because Razor Pages requires it, change their handler signatures; package validation records this intended break.
- The provider pages share a modern layout with light and dark themes following the system. The sign-in page names the requesting application; the error page shows the OAuth error, its description, the client and the request id. Their stylesheet and icon are embedded in the Runtime and served from the provider's origin, as the pages' CSP refuses inline styles and scripts.
- Client: `AddClientCredentialsHandler` sends client-credentials tokens on a named `HttpClient`, cached per client and scope until shortly before they expire, shared by concurrent requests and dropped when a resource answers 401. Each token request attempt carries a new assertion, so resilience handlers can stay in place. `ClientCredentials.CreateRequest` builds the request alone for other HTTP stacks; Sample.Api calls a partner API with the handler.
- Client: token endpoint errors of both handlers raise `TokenEndpointException`, an `HttpRequestException` carrying the OAuth `error` and `error_description` instead of a bare status code.
- New optional package `TheDotSide.Aspire.IdentityServer.Testing`: `GetUserTokensAsync` signs a test user in through the provider's authorization code + PKCE flow and sign-in form without a browser, `GetClientCredentialsTokensAsync` requests machine tokens, and `GetIdentityClientOptionsAsync` resolves a client's settings and credentials, on a started `DistributedApplication`. Publishing pushes it after Hosting and Client; the package smoke test uses it from an isolated test project.
- New optional package `TheDotSide.Aspire.IdentityServer.Authentication`: `AddIdentityServerJwtBearer` and `AddIdentityServerOpenIdConnect` configure ASP.NET Core from the projected sections, with JWT claim names, PKCE, and a fresh `private_key_jwt` assertion for the code redemption and pushed authorization requests (PAR), so PAR no longer needs to be disabled. `RequireScope` matches the space-delimited `scope` claim. The samples use them, and the web sample adds a `par` scheme that requires PAR with the private-key client.
- Hosting exposes `IdentityServerResource.RegisteredUsers`, `IdentityClient.GrantType` and `IdentityClient.RegisteredRedirectUris`.
- Clients gain `WithAccessTokenLifetime` and `WithRefreshTokenLifetime` (refresh lifetimes require `offline_access`), `WithAllowedCorsOrigin` for single-page applications, literal `WithRedirectUri(Uri)`/`WithPostLogoutRedirectUri(Uri)` for tools and native applications (HTTPS, HTTP loopback or RFC 8252 private-use schemes), and `WithBackChannelLogoutUri`/`WithFrontChannelLogoutUri`.
- The AppHost generates each provider's signing key as the secret parameter `{provider}-signing-key`: a provider restarted from the dashboard keeps it, so earlier tokens stay valid and consumers' cached keys keep matching.
- The web sample adds a dependency-free single-page application at `/spa/` (code redemption and UserInfo through CORS) and records back-channel logout tokens.
- Dashboard commands on each provider: **Show registrations** lists its clients, API resources, identity scopes and test users without secrets, and **Request a client token** requests and displays a client-credentials token, with its claims, for a chosen machine client. Aspire's resource command API makes them discoverable by other clients too.
- A weekly `compatibility` workflow builds the solution with the MSBuild of Visual Studio and, once published, the next SDK preview, which the provider folder target of ADR 0001 depends on.
- Token exchange between two providers (#4): `WithTokenExchangeFrom` accepts a source client of another provider, which declares a directional trust. The target provider validates the foreign token with keys discovered only from that issuer (bounded refresh on an unknown key), requires the declared source client and the usual scope rules, and issues a token for its own API whose subject is `{source provider}|{sub}`, so that equal subject IDs never collide. The sample AppHost adds a `partner` client of provider A trusting provider B's web users.
- Update Microsoft.Extensions dependencies, Meziantou.Analyzer and CSharpier. The package smoke test keeps the provider running while the independent test consumer signs in and validates its tokens.

### Breaking changes

- Hosting and Runtime share configuration contract 4 (signing key, lifetimes, CORS origins, logout URIs, trusted issuer and provider of cross-provider policies): use the same package versions.

## 11.0.0 - 2026-11-12

- Add explicit `net10.0` and `net11.0` assets to Hosting, Runtime and Client in the same NuGet packages. Preserve the public API and Hosting/Runtime contract 3.
- Validate public package API compatibility against stable 10.0.0, including the retained .NET 10 assets.
- Build the repository with SDK 11.0.100 and run the protocol/browser suite against both frameworks. Keep SDK 10 consumers supported and select a matching provider runtime configuration, including when both runtimes are installed.
- Use ASP.NET Core authentication 10.0.15 for .NET 10 and 11.0.0 for .NET 11; align the Client's Microsoft.Extensions dependencies per framework.
- Verify actual NuGet archives in independent AppHosts and distributed-test consumers built with SDK 10/.NET 10 and SDK 11/.NET 11, with isolated caches.
- Exercise OIDC `max_age` with private-key, secret and public clients without disabling the .NET 11 signed `auth_time` validation.
- Keep Duende 8.0.10 and Aspire 13.6.5. Document the distinction between this integration's .NET 11 tests and Duende's published .NET 10 support policy.
- Allow up to fifteen minutes for NuGet indexing before validating registry-only consumers, with a twenty-five-minute publication job limit.

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
