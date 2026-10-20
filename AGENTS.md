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

# Formatting and analysis

CSharpier is the only C#/XML whitespace formatter. Restore the pinned local tool with `dotnet tool restore`, format with `dotnet csharpier format .`, and verify with `dotnet csharpier check .`. Do not use `dotnet format whitespace` alongside it. `dotnet format analyzers` may apply semantic analyzer code fixes, followed by CSharpier.

Meziantou.Analyzer is a private global package reference in `Directory.Packages.props`. Keep the rule policy and justified exceptions in the root `.editorconfig`; preserve warnings-as-errors. Never add this development analyzer as a dependency of the published packages.

Run `pwsh eng/setup.ps1` once per checkout to restore local tools and enable the versioned `.githooks` directory. The pre-commit hook formats and re-stages only staged C#/XML files without unstaged changes. It rejects partially staged files without modifying their index or working content. Do not bypass this behavior by staging unrelated files.
