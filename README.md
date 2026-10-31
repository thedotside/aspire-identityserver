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
- [Tests, packaging and troubleshooting](docs/developpement.md)
- [Release tags and automated NuGet publishing](docs/publication.md)

The documentation lives in the repository's root `docs/` directory. From a NuGet package viewer, use the [online documentation](https://github.com/thedotside/aspire-identityserver/tree/main/docs).

## Quick example

In an AppHost referencing the `TheDotSide.Aspire.IdentityServer.Hosting` package:

```csharp

var builder = DistributedApplication.CreateBuilder(args);
var idp = builder.AddIdentityServer("idp");
idp.AddApiResource("orders-api").WithScopes("orders.read");
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");

// For an existing API/worker resource:
// api.WithIdentityClient(worker, "Integrations:Orders:OAuth");

builder.Build().Run();
```

## Packages and requirements

| Package | Purpose |
| --- | --- |
| `TheDotSide.Aspire.IdentityServer.Hosting` | AppHost API, secret parameters and projections. |
| `TheDotSide.Aspire.IdentityServer.Runtime` | Provider host, installed transitively by Hosting. |
| `TheDotSide.Aspire.IdentityServer.Client` | Optional configuration, assertion helpers and token-exchange handler for named HTTP clients. |

Version: `0.1.0-alpha.3`. Requires .NET 10, Aspire 13.6.4 or later and a trusted development HTTPS certificate. See [installation](docs/demarrage.md) for NuGet and local packages.

Local development/testing only: the AppHost must run in `Development` or `Testing`, state resets on restart and AppHost publication is rejected. A dedicated Duende BFF adapter and Kubernetes deployment of the provider are outside this alpha. Performance against Keycloak has not been benchmarked.

Original code: [MIT](LICENSE). Dependencies retain their own licenses, including Duende; see [third-party notices](THIRD-PARTY-NOTICES.md).
