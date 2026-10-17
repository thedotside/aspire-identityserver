# Aspire IdentityServer

Local OAuth 2.0 / OpenID Connect providers for **Aspire development and integration tests**. Declare two providers, clients, scopes and API audiences in your AppHost, and project their configuration into your API, web app, BFF or worker.

The server uses Duende IdentityServer, runs as a small .NET process, and stores configuration, users, grants and signing keys in memory. No database or container is required. Performance against Keycloak has **not** been benchmarked.

## Packages

| Package | Purpose |
| --- | --- |
| `Aspire.IdentityServer.Hosting` | Aspire integration, fluent registrations, secret parameters and configuration projections. Automatically references the runtime package and prepares the server beside the AppHost at build time. |
| `Aspire.IdentityServer.Runtime` | Our compiled provider host. Duende is a normal NuGet dependency, restored from its original package, not republished inside ours. Installed transitively. |
| `Aspire.IdentityServer.Client` | Optional configuration class and RSA `private_key_jwt` assertion helper. No Aspire or Duende dependency. |

The first version is `0.1.0-alpha.1`. Packages are generated locally; this repository does not imply publication or availability of these IDs on nuget.org. Requires .NET 10 and Aspire 13.6.1. The AppHost needs the normal ASP.NET Core development HTTPS certificate (`dotnet dev-certs https --trust`).

## AppHost

Install the Hosting package in your AppHost, then:

```csharp
using Aspire.IdentityServer.Hosting;

var idp = builder.AddIdentityServer("idp-a")
    .WithTestUser("alice", "alice", "Alice");

var orders = idp.AddApiResource("orders-api")
    .WithScopes("orders.read", "orders.write");

var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");

var web = builder.AddProject<Projects.Web>("web").WithHttpsEndpoint();
var browser = idp.AddAuthorizationCodeClient("web")
    .WithGeneratedPrivateKeyJwt()
    .RequirePkce()
    .WithScopes("openid", "profile", "orders.read")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-oidc");

web.WithIdentityClient(browser, "Authentication:Corporate");

builder.AddProject<Projects.Api>("api")
    .WithIdentityResource(orders, "Authentication:Inbound")
    .WithIdentityClient(worker, "Integrations:Accounting:OAuth");

// A second call creates an independent issuer, signing key and login session.
var secondIdp = builder.AddIdentityServer("idp-b");
```

Configure separate authentication schemes, cookie names and callback paths for each provider in your web app. See `samples/Sample.Web` for the complete working example.

`WithScopes` selects allowed scopes. `AddApiResource` associates those scopes with the access-token audience. There is no arbitrary client-side audience override. Interactive scopes `openid`, `profile` and `email` are available; test users currently include a `name` claim.

## Client authentication

Choose one authentication method per client:

```csharp
client.WithGeneratedPrivateKeyJwt();              // Per-client RSA key, one per AppHost run
client.WithPrivateKeyJwt(privateKeyPem);          // Existing RSA private key, PEM
client.WithPrivateKeyJwt(keyParameter, publicJwk);// Secret parameter and matching public RSA JWK
client.WithGeneratedClientSecret();              // Random secret, one per run
client.WithClientSecret(builder.AddParameter("oauth-secret", secret: true));
```

The public JWK overload requires an RSA key of at least 2048 bits and a `kid`. The supplied private key must match it. A public interactive client simply omits the authentication method and requires PKCE. **PKCE is independent of client authentication**: a confidential client can use both PKCE and `private_key_jwt`.

Private keys and secrets are represented by Aspire secret parameters. Only the public JWK goes to the provider. A client's private key is only projected to explicitly selected consumers, and is distinct from the provider's token-signing key. The parameter transport uses process environment variables, not a production secret store. Do not log bound client options or commit development keys.

## Configuration projections

The default projection writes `Authority`, `ClientId`, `AuthenticationMethod`, `Scopes` (space-delimited), and the applicable `ClientSecret` or `PrivateKeyPem` / `KeyId` fields into your chosen section. Complete client registration before projecting it. Callback addresses retain Aspire endpoint references until ports have been allocated. Consumers wait for their provider's health check.

Map to an existing component's own configuration schema:

```csharp
api.WithIdentityClient(worker, p => p
    .Authority("Outbound:Accounting:Issuer")
    .ClientId("Outbound:Accounting:Client")
    .PrivateKeyPem("Outbound:Accounting:SigningKey")
    .KeyId("Outbound:Accounting:KeyId")
    .Scopes("Outbound:Accounting:RequestedScopes"));

// Multiple clients or multiple projections can target different sections in the same application.
api.WithIdentityClient(otherClient, "Integrations:Warehouse:OAuth");

// Native Aspire wiring remains available.
api.WithEnvironment("CUSTOM_PRIVATE_KEY", worker.PrivateKeyPem!);
```

An inbound API only needs `WithIdentityResource` (authority and audience). It needs a client key only when it also acts as an outbound OAuth client.

The receiving app configures its own OIDC/token library. Projecting a PEM string does not itself enable client assertions. For Duende BFF, map the fields into the section read by your BFF/OIDC and access-token-management setup. The sample exercises the standard ASP.NET Core OIDC handler; a dedicated Duende BFF adapter is not included in this alpha.

The optional helper signs a **new assertion for every token request**:

```csharp
var client = configuration.GetSection("Integrations:Accounting:OAuth")
    .Get<IdentityClientOptions>()!;
var assertion = PrivateKeyJwt.CreateAssertion(client, tokenEndpointFromDiscovery);
// Token form fields:
// client_id, grant_type, scope,
// client_assertion_type = PrivateKeyJwt.AssertionType,
// client_assertion = assertion
```

For interactive clients, see `OnAuthorizationCodeReceived` in `Sample.Web`. The sample disables PAR, so assertions are added to the token request. If enabling PAR with another client library, configure authentication for that request too.

## Develop and test

```powershell
dotnet restore
dotnet build
pwsh tests/Aspire.IdentityServer.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --no-build
dotnet run --project samples/Sample.AppHost
```

Building prepares an `identityserver` directory beside the AppHost and test assembly using NuGet-resolved dependencies. There is no separate server publish step. Sign in with `alice` / `alice`. Both providers run on Aspire-allocated HTTPS ports; `Alice A` and `Alice B` identify their sessions.

Tests share one real Aspire AppHost, use randomized ports, and exercise actual Chromium redirects, PKCE, login/logout, both client-authentication methods, API token validation and provider isolation. Negative cases cover wrong signatures, assertion audience, replay and unauthorized scopes. A local startup measurement is diagnostic, not a comparative benchmark.

Local validation on Windows, 2026-10-17: 21 tests passed, all three packages built, and an independent package consumer started a provider and obtained a token using `private_key_jwt`. The GitHub Actions workflow is supplied but has not been executed remotely. Other operating systems have not been verified.

## Pack and reuse

```powershell
pwsh ./eng/pack.ps1
pwsh ./eng/package-smoke.ps1
```

Packages are written to `artifacts/packages`. The smoke test creates a temporary independent AppHost, references the `.nupkg` through a local feed, and runs it without project references to this repository.

Add `artifacts/packages` as a NuGet source in a consuming solution, then reference `Aspire.IdentityServer.Hosting` version `0.1.0-alpha.1`. Add `Aspire.IdentityServer.Client` to consumers only if useful.

For a release, choose a new immutable version, run tests and the package smoke test, review package contents and third-party terms, then publish the three packages with your own NuGet credentials. CI builds, tests and uploads package artifacts; it never publishes them automatically. No official Aspire/CommunityToolkit affiliation is implied.

## Current boundaries

Local .NET consumers on the same machine; HTTPS only. Runtime state and provider signing keys reset on restart. RSA/RS256 client assertions, authorization code + PKCE, and client credentials are supported. Persistent users, refresh tokens, federation, custom claims, container-network issuer rewriting and production deployment are outside this alpha. Publishing an AppHost with the resource is rejected explicitly. The server also rejects environments other than Development/Testing.

Aspire's HTTPS certificate configuration callback is currently experimental (`ASPIRECERTIFICATES001`). Its use is contained inside the Hosting integration; rerun the full suite and package smoke test when upgrading Aspire.

This project's original code is MIT licensed. Duende dependencies keep their own [license terms](https://duendesoftware.com/license); see [third-party notices](THIRD-PARTY-NOTICES.md). Our packages contain only our assemblies. Distributing the built application with Duende binaries is subject to Duende's separate redistribution terms.
