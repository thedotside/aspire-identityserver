# Repository workflow

This is a local-development OAuth/OIDC integration for Aspire. It must validate real protocols; never make tokens or client assertions permissive to simplify tests.

- `src/Aspire.IdentityServer.Hosting`: AppHost API and configuration projections. Preserve deferred endpoint references and Aspire secret parameters.
- `src/Aspire.IdentityServer.Server`: independent Duende process, published as the `Aspire.IdentityServer.Runtime` NuGet dependency. Local development/testing only.
- `src/Aspire.IdentityServer.Client`: optional, engine-independent assertion helper. Generate a fresh `jti` for each request.
- `samples`: two providers, three browser clients and two machine clients.
- `tests`: one shared real AppHost and isolated browser contexts, plus projection contract tests.

Use .NET 10 and aligned Aspire 13.6.1 hosting/testing/SDK packages. Run `dotnet build`, `dotnet test`, `pwsh eng/pack.ps1`, then `pwsh eng/package-smoke.ps1` for changes affecting packaging. The smoke script consumes actual packages in an isolated temporary project/cache. No Docker dependency.

NuGet archives contain only this repository's assemblies. Duende and other third-party code must remain ordinary NuGet dependencies, with their original licenses. Do not bundle those binaries or claim our MIT license covers them. Do not publish packages or push changes unless requested.

Never place keys, client secrets, tokens or test traces in tracked files. `artifacts`, `work`, `bin` and `obj` are ignored. Built runtime folders can contain restored third-party assemblies and must not be published as project-owned software.

Do not claim lower RAM/startup time than Keycloak without a reproducible comparison. An Aspire startup measurement alone is not that comparison. Keep platform/CI validation claims limited to runs actually completed.
