# Third-party software

The integration and client helper in this repository are MIT licensed. This does not relicense their dependencies.

The `TheDotSide.Aspire.IdentityServer.Runtime` package depends on **Duende IdentityServer 8.0.10**. NuGet restores the original Duende packages on the consumer's machine. Our `.nupkg` files do not bundle Duende binaries, source code or license keys. The build copies restored assemblies into a local process directory to run the provider.

Duende IdentityServer, IdentityServer.Storage and Private.Licensing retain the [Duende Software license](https://duendesoftware.com/license). Read it before any use. At the time of writing, free use is limited to development, testing and internal evaluation, and the license restricts making the software available for third parties to install or host. This integration supports only local development and test scenarios. Whether publishing these packages publicly is compatible with Duende's terms has not been confirmed by Duende. Distribution of an application containing Duende binaries is a separate matter governed by their terms; our MIT license grants no right on Duende software.

Microsoft .NET, ASP.NET Core, IdentityModel, OpenTelemetry, Aspire, MessagePack and testing dependencies retain the licenses published with their original NuGet packages. Dependency versions are declared in `Directory.Packages.props` and resolved in `obj/project.assets.json`.
