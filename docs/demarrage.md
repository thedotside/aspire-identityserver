# Démarrage

[Documentation](README.md) · [Suite : fournisseurs](fournisseurs.md)

## Prérequis

- SDK .NET 10. Le dépôt demande `10.0.203` avec `rollForward: latestFeature` dans [global.json](../global.json).
- Packages et SDK Aspire alignés sur `13.6.1`.
- PowerShell 7 pour les scripts `eng/*.ps1`.
- Certificat HTTPS de développement approuvé sur la machine.
- Chromium installé par Playwright pour exécuter les tests navigateur.

Les IDP et applications exemples démarrent comme des processus .NET. Docker et une base de données ne sont pas nécessaires. Windows est la plateforme validée localement ; les autres systèmes ne sont pas encore vérifiés pour ce dépôt.

## Lancer les exemples du dépôt

Depuis la racine :

```powershell
dotnet dev-certs https --trust
dotnet restore
dotnet build
dotnet run --project samples/Sample.AppHost
```

Sous Windows, approuve la fenêtre de confirmation du certificat si elle apparaît. La CI sans bureau dispose d'une préparation distincte dans le workflow du dépôt.

Ouvre la ressource `web` depuis le dashboard Aspire. Le sample propose trois parcours :

| Parcours | Fournisseur | Authentification du client | PKCE |
| --- | --- | --- | --- |
| `a` | `idp-a` | `private_key_jwt` | Oui. |
| `b` | `idp-b` | Secret | Oui. |
| `public` | `idp-a` | Aucune | Oui. |

Le compte de démonstration est `alice` / `alice`. Le nom affiché est `Alice A` ou `Alice B` suivant le fournisseur. Ces identifiants sont exclusivement destinés au sample local.

L'API présente aussi deux clients machine et deux configurations de clients sortants. Voir [Sample.AppHost](../samples/Sample.AppHost/Program.cs), [Sample.Web](../samples/Sample.Web/Program.cs) et [Sample.Api](../samples/Sample.Api/Program.cs).

## Installer dans une autre solution

Les trois packages sont construits localement en `0.1.0-alpha.1`. Vérifie leur présence dans ta source NuGet ; ce guide ne suppose pas leur publication sur nuget.org.

Depuis ce dépôt :

```powershell
pwsh eng/pack.ps1
```

Depuis la solution consommatrice, ajoute le répertoire `artifacts/packages` de ce dépôt comme source. Remplace le chemin par celui de ta copie :

```powershell
dotnet nuget add source "C:/chemin/aspire-identityserver/artifacts/packages" --name aspire-identityserver-local
dotnet add MonAppHost/MonAppHost.csproj package Aspire.IdentityServer.Hosting --version 0.1.0-alpha.1
```

`Runtime` est installé transitivement par `Hosting`. Ajoute le helper à l'application consommatrice si elle utilise ses options ou crée des assertions :

```powershell
dotnet add MonApplication/MonApplication.csproj package Aspire.IdentityServer.Client --version 0.1.0-alpha.1
```

Avec Central Package Management, déclare les versions dans `Directory.Packages.props` et les références sans version dans les projets.

## Forme du projet AppHost

Pour une solution indépendante sans Central Package Management, voici le modèle utilisé par le smoke test :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.6.1" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
    <AspireUseCliBundle>true</AspireUseCliBundle>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.6.1" />
    <PackageReference Include="Aspire.IdentityServer.Hosting" Version="0.1.0-alpha.1" />
  </ItemGroup>
</Project>
```

`AspireUseCliBundle` fournit les outils Aspire nécessaires au lancement. Les projets API/web conservent leurs références normales.

## Premier fournisseur et client machine

Ce `Program.cs` démarre un fournisseur avec une ressource API et un client machine :

```csharp
using Aspire.IdentityServer.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var idp = builder.AddIdentityServer("idp");
idp.AddApiResource("orders-api").WithScopes("orders.read");
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");

builder.Build().Run();
```

Il enregistre le client mais ne demande pas de token. La [page d'intégration](integration.md) montre comment un consommateur le fait.

Si une ressource `api` existe déjà dans cet AppHost :

```csharp
api.WithIdentityClient(worker, "Integrations:Orders:OAuth");
```

La projection attend que le fournisseur soit sain et transmet ses paramètres au processus consommateur. Elle ne configure pas le middleware d'authentification de l'application.

## Préparation du runtime

Au build, `Hosting` copie les assemblies résolues par NuGet dans `identityserver`, à côté de l'AppHost. Les projets de test bénéficient du même mécanisme. `AddIdentityServer()` y démarre `dotnet Aspire.IdentityServer.Server.dll`.

Il n'y a pas de publication manuelle du serveur à effectuer avec les packages. Les références projet du dépôt importent explicitement le fichier `.targets`, car une référence projet n'importe pas les assets `buildTransitive` d'un package.

Si le runtime est introuvable, consulte [le dépannage](developpement.md) et [RuntimeDirectory](fournisseurs.md).
