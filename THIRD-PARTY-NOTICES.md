# Third-party software

The integration and client helper in this repository are MIT licensed. This does not relicense their dependencies.

The `TheDotSide.Aspire.IdentityServer.Runtime` package depends on **Duende IdentityServer 8.0.10**. NuGet restores the original Duende packages on the consumer's machine. Our `.nupkg` files do not bundle Duende binaries, source code or license keys. The build copies restored assemblies into a local process directory to run the provider.

Duende IdentityServer, IdentityServer.Storage and Private.Licensing retain the [Duende Software license](https://duendesoftware.com/license). Duende documents IdentityServer as free for [development and testing](https://docs.duendesoftware.com/general/licensing/). This integration supports only those local scenarios. Distribution of an application containing Duende binaries is a separate matter governed by their redistribution terms; our MIT license does not grant those rights.

Microsoft .NET, ASP.NET Core, IdentityModel, Aspire, MessagePack and testing dependencies retain the licenses published with their original NuGet packages. Dependency versions are declared in `Directory.Packages.props` and resolved in `obj/project.assets.json`.
