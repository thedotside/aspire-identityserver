# Aspire IdentityServer

Local OAuth 2.0 / OpenID Connect providers for **Aspire development and integration tests**. Configure independent identity providers, test users, browser clients, machine clients, scopes and API audiences from your AppHost.

Built on Duende IdentityServer, with in-memory state and no database or container requirement. Supports authorization code + PKCE, client secrets, RSA `private_key_jwt`, and configuration projections into APIs, web apps, BFFs and workers.

## Documentation

**[Complete configuration guide / documentation en français](docs/README.md)**

- [Getting started and installation](docs/demarrage.md)
- [Providers, users, scopes and audiences](docs/fournisseurs.md)
- [Clients, PKCE, secrets and private keys](docs/clients.md)
- [Base64 private RSA JWK: BFF, CC Xp/Capa and Kubernetes](docs/jwk-base64.md)
- [Same-provider Token Exchange](docs/token-exchange.md)
- [Projections: AppHost → environment → .NET configuration](docs/projections.md)
- [API, OIDC, BFF and outbound client integration](docs/integration.md)
- [Kubernetes configuration and deployment boundaries](docs/kubernetes.md)
- [Complete API reference](docs/reference.md)
- [Integration tests with the Testing package](docs/tests.md)
- [Tests, packaging and troubleshooting](docs/developpement.md)
- [Release tags and automated NuGet publishing](docs/publication.md)
- [.NET 10 / .NET 11 compatibility and migration](docs/compatibilite-dotnet.md)

The documentation lives in the repository's root `docs/` directory, which requires access to the repository. This README covers the essentials for package users.

## Quick example

Start an AppHost in `Development` or `Testing` with `TheDotSide.Aspire.IdentityServer.Hosting` and `Aspire.Hosting.Dotnet` referenced for this example.

```csharp
var builder = DistributedApplication.CreateBuilder(args);
var idp = builder.AddIdentityServer("idp").WithTestUser("alice", "alice");
var orders = idp.AddApiResource("orders-api").WithScopes("orders.read");
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");

var api = builder.AddDotnetProject("api", "../Orders.Api/Orders.Api.csproj")
    // Inbound validation: Authentication:Inbound:Authority and :Audience.
    .WithIdentityApiResource(orders, "Authentication:Inbound")
    // Outbound client: Authority, ClientId, PrivateKeyPem, KeyId, Scopes...
    .WithIdentityClient(worker, "Integrations:Orders:OAuth");

builder.Build().Run();
```

No `using` is needed: the extensions live in `Aspire.Hosting`, like other Aspire integrations. Keys and secrets stay Aspire secret parameters; the provider only receives public keys and secret hashes.

`WithIdentityScope("communities", "member_of")` declares an identity scope requested only by Authorization Code clients, such as a BFF. Since alpha.5, the granted identity scopes select user claims in the login access token as well as the ID token and UserInfo. YARP can relay that access token directly to Xp; without Xp, the BFF uses token exchange for Capa. `communities` is never an API or machine-client scope. See the [complete example and alpha.4 migration](docs/fournisseurs.md#claims-des-scopes-didentité-dans-laccess-token).

`WithSimulatedMfa("111111", "urn:example:loa:mfa")` simulates a second factor: a client that requests that level in `acr_values` is asked for the code after the password, then receives the level as `acr`, with `pwd`, `otp` and `mfa` as `amr`. Applications keep their MFA checks enabled locally. See [simulated MFA](docs/fournisseurs.md#mfa-simulée).

In the consumer, the optional `TheDotSide.Aspire.IdentityServer.Client` package binds the projected section and signs `private_key_jwt` assertions with the [draft-ietf-oauth-rfc7523bis](https://datatracker.ietf.org/doc/draft-ietf-oauth-rfc7523bis/) profile:

```csharp
var client = builder.Configuration.GetSection("Integrations:Orders:OAuth").Get<IdentityClientOptions>()!;
// issuer: the "issuer" value of the provider's discovery document.
var assertion = PrivateKeyJwt.CreateAssertion(client, issuer);
```

`AddClientCredentialsHandler` adds cached client-credentials tokens to a named `HttpClient`, and `AddTokenExchangeHandler` RFC 8693 token exchange; token endpoint errors surface as `TokenEndpointException` with their OAuth `error`.

## Packages and requirements

| Package | Purpose |
| --- | --- |
| `TheDotSide.Aspire.IdentityServer.Hosting` | AppHost API, secret parameters and projections. |
| `TheDotSide.Aspire.IdentityServer.Runtime` | Provider host, installed transitively by Hosting. |
| `TheDotSide.Aspire.IdentityServer.Client` | Optional configuration, assertion helpers, and client-credentials and token-exchange handlers for named HTTP clients. |
| `TheDotSide.Aspire.IdentityServer.Authentication` | Optional ASP.NET Core helpers: `AddIdentityServerJwtBearer`, `AddIdentityServerOpenIdConnect` (PKCE, secret or `private_key_jwt`, PAR included) and `RequireScope`. |
| `TheDotSide.Aspire.IdentityServer.Testing` | Optional integration-test helpers: user tokens through the real sign-in without a browser, client-credentials tokens and resolved client options. |

Version: `11.3.1` (in development). Targets .NET 10 and .NET 11, with Aspire 17.0.0 or later and a trusted development HTTPS certificate. Building this repository requires SDK 11 and the .NET 10 runtime as well. Package consumers can keep SDK 10 when targeting .NET 10. See [installation](docs/demarrage.md) for published and local packages.

See the [11.1.0 changelog](CHANGELOG.md#1110---2026-11-20) for the new integration features. Upgrade Hosting, Runtime and the optional Client, Authentication and Testing packages together. The major number identifies the .NET release family. See [compatibility and migration](docs/compatibilite-dotnet.md) for supported frameworks and Duende's support policy.

Local development/testing only: the AppHost must run in `Development` or `Testing`, state resets on restart and AppHost publication is rejected. A dedicated Duende BFF adapter and Kubernetes deployment of the provider are outside this integration's scope. Performance against Keycloak has not been benchmarked.

## License

Original code: [MIT](LICENSE). Dependencies retain their own licenses. The provider runs **Duende IdentityServer**, under the [Duende Software license](https://duendesoftware.com/license): free use is limited to development, testing and internal evaluation. Read it before installing these packages, and never run this provider in production. See the [third-party notices](THIRD-PARTY-NOTICES.md).
