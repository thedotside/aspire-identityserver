# Démarrage

[Documentation](README.md) · [Suite : fournisseurs](fournisseurs.md)

## Prérequis

- Pour construire le dépôt : SDK .NET 11, `11.0.100` avec `rollForward: latestFeature` dans [global.json](../global.json), et runtime ASP.NET Core .NET 10 pour exécuter aussi les tests `net10.0`.
- Pour consommer les packages : SDK 10 avec une application `net10.0`, ou SDK 11 avec une application `net11.0`. Les deux cibles sont conservées dans les mêmes packages ; voir [la compatibilité](compatibilite-dotnet.md).
- Aspire `13.6.5` ou ultérieur : c'est le plancher du package Hosting.
- PowerShell 7 pour les scripts `eng/*.ps1`.
- Certificat HTTPS de développement approuvé sur la machine.
- Chromium installé par Playwright pour exécuter les tests navigateur.

Le fournisseur exécute **Duende IdentityServer**, sous la [licence de Duende Software](https://duendesoftware.com/license), distincte de la licence MIT de ce dépôt. Son usage gratuit est limité au développement, aux tests et à l'évaluation internes : lis cette licence avant d'installer les packages, et n'utilise jamais ce fournisseur en production. Voir les [notices tierces](../THIRD-PARTY-NOTICES.md).

Les IDP et applications exemples démarrent comme des processus .NET. Docker et une base de données ne sont pas nécessaires. La CI exécute la validation complète sous Windows et Linux ; le dernier passage local consigné l'a été sous macOS arm64. Voir [le développement](developpement.md#exécuter-les-tests).

## Lancer les exemples du dépôt

Depuis la racine :

```powershell
dotnet dev-certs https --trust
dotnet restore
dotnet build
dotnet run --project samples/Sample.AppHost --framework net10.0
```

Sous Windows, approuve la fenêtre de confirmation du certificat si elle apparaît. La CI sans bureau dispose d'une préparation distincte dans le workflow du dépôt.

Utilise `--framework net11.0` pour lancer les exemples sous .NET 11. Le choix est obligatoire pour les projets exécutables qui ciblent plusieurs frameworks.

Ouvre la ressource `web` depuis le dashboard Aspire. Le sample propose trois parcours :

| Parcours | Fournisseur | Authentification du client | PKCE |
| --- | --- | --- | --- |
| `a` | `idp-a` | `private_key_jwt` | Oui. |
| `b` | `idp-b` | Secret | Oui. |
| `public` | `idp-a` | Aucune | Oui. |

Le compte de démonstration est `alice` / `alice`. Le nom affiché est `Alice A` ou `Alice B` suivant le fournisseur. Ces identifiants sont exclusivement destinés au sample local.

L'API présente aussi deux clients machine et deux configurations de clients sortants. Voir [Sample.AppHost](../samples/Sample.AppHost/Program.cs), [Sample.Web](../samples/Sample.Web/Program.cs) et [Sample.Api](../samples/Sample.Api/Program.cs).

## Installer dans une autre solution

Pour installer la version stable `10.0.0` sur nuget.org :

```powershell
dotnet add MonAppHost/MonAppHost.csproj package TheDotSide.Aspire.IdentityServer.Hosting --version 10.0.0 --source https://api.nuget.org/v3/index.json
```

`Runtime` est installé transitivement par `Hosting`. Le helper `TheDotSide.Aspire.IdentityServer.Client` est facultatif et s'installe dans l'application consommatrice, à la même version. Aucun filtre de préversions n'est nécessaire pour `10.0.0`. Depuis `0.1.0-alpha.5`, la migration consiste à aligner les versions des packages ; l'API et le contrat restent identiques. La [publication par tag](publication.md) explique comment vérifier qu'une version est disponible.

Pour consommer des packages construits localement avant une publication, utilise la procédure suivante.

Depuis ce dépôt :

```powershell
pwsh eng/pack.ps1
```

Depuis la solution consommatrice, ajoute le répertoire `artifacts/packages` de ce dépôt comme source. Remplace le chemin par celui de ta copie :

```powershell
dotnet nuget add source "C:/chemin/aspire-identityserver/artifacts/packages" --name aspire-identityserver-local
dotnet add MonAppHost/MonAppHost.csproj package TheDotSide.Aspire.IdentityServer.Hosting --version 11.0.0
```

`Runtime` est installé transitivement par `Hosting`. Ajoute le helper à l'application consommatrice si elle utilise ses options ou crée des assertions :

```powershell
dotnet add MonApplication/MonApplication.csproj package TheDotSide.Aspire.IdentityServer.Client --version 11.0.0
```

Avec Central Package Management, déclare les versions dans `Directory.Packages.props` et les références sans version dans les projets.

## Forme du projet AppHost

Pour une solution indépendante sans Central Package Management, voici le modèle utilisé par le smoke test :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="13.6.5" />
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
    <AspireUseCliBundle>true</AspireUseCliBundle>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.6.5" />
    <PackageReference Include="TheDotSide.Aspire.IdentityServer.Hosting" Version="11.0.0" />
  </ItemGroup>
</Project>
```

`AspireUseCliBundle` fournit les outils Aspire nécessaires au lancement. Les projets API/web conservent leurs références normales.

Le même modèle fonctionne avec `<TargetFramework>net11.0</TargetFramework>` et le SDK .NET 11. La version `11.0.0` est encore en développement : les commandes ci-dessus consomment les packages locaux, pas une version déjà publiée sur nuget.org.

## Premier fournisseur et client machine

Ce `Program.cs` démarre un fournisseur avec une ressource API et un client machine :

```csharp

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

## Environnement de l'AppHost et publication

`AddIdentityServer()` n'accepte qu'un AppHost démarré en `Development` ou `Testing`. Le profil `launchSettings.json` d'un AppHost Aspire fixe déjà `Development` ; un AppHost lancé autrement, par son binaire, un script ou la CI, doit définir `DOTNET_ENVIRONMENT`. Le fournisseur reçoit ce nom réel, refuse lui aussi tout autre environnement et journalise un avertissement à chaque démarrage.

`aspire publish` échoue dès qu'un fournisseur est déclaré. Pour garder un AppHost publiable, déclare le fournisseur et ses projections seulement en mode exécution. En publication, les consommateurs reçoivent la configuration du fournisseur réel par leurs propres paramètres ; voir [Kubernetes](kubernetes.md).

```csharp
if (builder.ExecutionContext.IsRunMode)
{
    var idp = builder.AddIdentityServer("idp");
    idp.AddApiResource("orders-api").WithScopes("orders.read");
    var worker = idp.AddClientCredentialsClient("worker")
        .WithGeneratedPrivateKeyJwt()
        .WithScopes("orders.read");
    api.WithIdentityClient(worker, "Integrations:Orders:OAuth");
}
```

## Préparation du runtime

Au build, `Hosting` copie dans `identityserver`, à côté de l'AppHost, les seules assemblies de la fermeture du paquet `Runtime`, dans les versions que NuGet a résolues pour ce projet, sans `deps.json`. Les projets de test bénéficient du même mécanisme. La [décision 0001](decisions/0001-dossier-runtime-du-fournisseur.md) en donne les raisons et les limites. `AddIdentityServer()` y démarre `Aspire.IdentityServer.Server.dll` avec l'hôte `dotnet` du runtime qui exécute l'AppHost, et non le premier `dotnet` du PATH : un poste peut avoir plusieurs installations de .NET. À défaut, il utilise `DOTNET_HOST_PATH`, puis le PATH.

Il n'y a pas de publication manuelle du serveur à effectuer avec les packages. Les références projet du dépôt importent explicitement le fichier `.targets`, car une référence projet n'importe pas les assets `buildTransitive` d'un package.

Si le runtime est introuvable, consulte [le dépannage](developpement.md) et [RuntimeDirectory](fournisseurs.md).
