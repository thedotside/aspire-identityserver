# Projections : AppHost, environnement et configuration .NET

[Documentation](README.md) · [Clients](clients.md) · [Intégration](integration.md) · [Kubernetes](kubernetes.md)

## Ce que fait une projection

`WithIdentityClient` relie un client enregistré à une ressource consommatrice. Elle ajoute des variables d'environnement au processus et une dépendance de démarrage vers l'IDP. Elle ne crée ni ne modifie `appsettings.json`.

```mermaid
flowchart LR
    A[Client déclaré dans AppHost] --> B[WithIdentityClient]
    B --> C[Variables du consommateur]
    C --> D[IConfiguration .NET]
    D --> E[Options de la bibliothèque OAuth/OIDC]
```

Les méthodes acceptent une ressource Aspire implémentant `IResourceWithEnvironment` et `IResourceWithWaitSupport`. Les samples utilisent des projets .NET. Le support des consommateurs dans des réseaux de conteneurs n'est pas validé : l'issuer actuel reste local.

Le consommateur attend le health check du fournisseur. L'endpoint reste un `EndpointReference` et la clé privée reste une référence de paramètre secret jusqu'à leur résolution par Aspire.

## Projection standard

Extrait après déclaration complète d'un client `browserA` et d'une ressource `web` :

```csharp
web.WithIdentityClient(browserA, "Authentication:IdpA");
```

| Champ .NET dans la section | Variable d'environnement | Valeur / condition |
| --- | --- | --- |
| `Authority` | `Authentication__IdpA__Authority` | Endpoint HTTPS du fournisseur. |
| `ClientId` | `Authentication__IdpA__ClientId` | Identifiant du client. |
| `AuthenticationMethod` | `Authentication__IdpA__AuthenticationMethod` | `none`, `client_secret_post` ou `private_key_jwt`. |
| `Scopes` | `Authentication__IdpA__Scopes` | Chaîne séparée par des espaces. |
| `ClientSecret` | `Authentication__IdpA__ClientSecret` | Seulement pour un client avec secret. |
| `PrivateKeyPem` | `Authentication__IdpA__PrivateKeyPem` | Seulement pour un client avec clé privée. |
| `KeyId` | `Authentication__IdpA__KeyId` | Avec la clé privée. |

`Scopes` est une chaîne, pas un tableau JSON. L'ordre des scopes ne constitue pas un contrat ; leur ensemble est significatif.

La projection ne contient pas `Audience`, `RequirePkce`, les redirect URIs, le token endpoint, le JWK public ni la clé de signature du fournisseur. Le consommateur configure ses callbacks et PKCE, et découvre les endpoints via OIDC. Pour la validation entrante d'une API, utilise `WithIdentityResource`.

## Exemple complet des trois clients du sample

Le [sample AppHost](../samples/Sample.AppHost/Program.cs) enregistre :

- `browserA` : client ID `web`, IDP A, `private_key_jwt`, scopes `openid profile orders.read`.
- `browserB` : client ID `web`, IDP B, secret, mêmes scopes.
- `publicClient` : client ID `public-web`, IDP A, aucune authentification client, scopes `openid profile`.

```csharp
web.WithIdentityClient(browserA, "Authentication:IdpA")
    .WithIdentityClient(browserB, "Authentication:IdpB")
    .WithIdentityClient(publicClient, "Authentication:Public");
```

Voici l'équivalent JSON de la configuration effective. Les adresses et credentials sont des marqueurs, pas des valeurs utilisables :

```json
{
  "Authentication": {
    "IdpA": {
      "Authority": "https://localhost:<port-idp-a>",
      "ClientId": "web",
      "AuthenticationMethod": "private_key_jwt",
      "Scopes": "openid profile orders.read",
      "PrivateKeyPem": "<cle-privee-PEM>",
      "KeyId": "<kid-client-a>"
    },
    "IdpB": {
      "Authority": "https://localhost:<port-idp-b>",
      "ClientId": "web",
      "AuthenticationMethod": "client_secret_post",
      "Scopes": "openid profile orders.read",
      "ClientSecret": "<secret-client-b>"
    },
    "Public": {
      "Authority": "https://localhost:<port-idp-a>",
      "ClientId": "public-web",
      "AuthenticationMethod": "none",
      "Scopes": "openid profile"
    }
  }
}
```

Ce JSON sert à expliquer le résultat ; ne le recopie pas avec les marqueurs dans un véritable fichier de configuration. Les variables d'environnement sont injectées par Aspire.

Avec la configuration .NET standard, `__` est traduit en `:`. Les variables d'environnement remplacent les mêmes clés des fichiers `appsettings*.json` ; les arguments de ligne de commande ont normalement une priorité supérieure. Si l'application remplace l'ordre des providers de configuration, vérifie ses règles de priorité.

La projection n'efface pas les anciennes valeurs d'un fichier. Si un fichier contient `ClientSecret` pour un client devenu public, l'absence de projection de ce champ ne le supprime pas. Évite les anciens credentials dans les fichiers et utilise `AuthenticationMethod` pour décider comment authentifier le client.

## Lire la configuration

Dans un projet ASP.NET Core référençant `Aspire.IdentityServer.Client` :

```csharp
using Aspire.IdentityServer.Client;

var builder = WebApplication.CreateBuilder(args);
var clientA = builder.Configuration
    .GetSection("Authentication:IdpA")
    .Get<IdentityClientOptions>()
    ?? throw new InvalidOperationException("Missing IdpA client configuration.");

builder.Services.Configure<IdentityClientOptions>("IdpA",
    builder.Configuration.GetSection("Authentication:IdpA"));
```

La première lecture donne un objet directement utilisable. La seconde enregistre des options nommées à lire via `IOptionsMonitor<IdentityClientOptions>.Get("IdpA")`. Enregistrer l'un ou l'autre ne configure pas automatiquement OIDC ni la récupération de tokens.

Tu peux aussi lire `configuration["Authentication:IdpA:PrivateKeyPem"]` ou utiliser ton propre type d'options. Une chaîne PEM contient de vrais retours à la ligne dans la valeur injectée. Dans une représentation JSON, ces retours sont échappés en `\n`.

## Adapter les noms à un composant existant

Extrait où `api` et `workerA` existent déjà :

```csharp
api.WithIdentityClient(workerA, p => p
    .Authority("Outbound:Accounting:Issuer")
    .ClientId("Outbound:Accounting:Client")
    .AuthenticationMethod("Outbound:Accounting:Method")
    .PrivateKeyPem("Outbound:Accounting:SigningKey")
    .KeyId("Outbound:Accounting:KeyId")
    .Scopes("Outbound:Accounting:RequestedScopes"));
```

| Chemin du composant | Variable réellement produite |
| --- | --- |
| `Outbound:Accounting:Issuer` | `Outbound__Accounting__Issuer` |
| `Outbound:Accounting:Client` | `Outbound__Accounting__Client` |
| `Outbound:Accounting:Method` | `Outbound__Accounting__Method` |
| `Outbound:Accounting:SigningKey` | `Outbound__Accounting__SigningKey` |
| `Outbound:Accounting:KeyId` | `Outbound__Accounting__KeyId` |
| `Outbound:Accounting:RequestedScopes` | `Outbound__Accounting__RequestedScopes` |

Seuls les champs sélectionnés dans la lambda sont projetés. Cette surcharge n'ajoute pas implicitement les champs de la projection standard. Les sept méthodes disponibles sont `Authority`, `ClientId`, `AuthenticationMethod`, `Scopes`, `ClientSecret`, `PrivateKeyPem` et `KeyId`.

Tu peux employer un nom de variable plat, par exemple `.PrivateKeyPem("ACCOUNTING_PRIVATE_KEY")`, ou un chemin avec `:`/`__`. Les chemins contenant `:` sont normalisés en `__`.

Avec des noms personnalisés comme `Issuer` ou `SigningKey`, `Get<IdentityClientOptions>()` ne renomme pas ces champs. Utilise le type d'options du composant ou construis explicitement `IdentityClientOptions` comme dans [Sample.Api](../samples/Sample.Api/Program.cs).

## Plusieurs composants et plusieurs applications

Extrait pour deux clients déjà enregistrés :

```csharp
api.WithIdentityClient(workerA, "Integrations:Accounting:OAuth")
    .WithIdentityClient(workerB, "Integrations:Warehouse:OAuth");

// Même client, autre composant de l'API :
api.WithIdentityClient(workerA, "Jobs:OrderExport:OAuth");

// Même client, autre processus :
web.WithIdentityClient(workerA, "Integrations:Orders:OAuth");
```

Les projections du même client partagent ses credentials ; elles ne créent pas de nouveaux enregistrements. Déclare des clients distincts si tu souhaites des identités ou des clés distinctes.

Chaque destination doit avoir un chemin propre. Deux projections qui écrivent une clé déjà présente échouent à l'évaluation de l'environnement ; elles ne remplacent pas silencieusement les credentials. Une destination répétée dans une même lambda est également refusée après normalisation.

## API entrante et client sortant

Une API qui reçoit des tokens n'a pas besoin de la clé privée d'un client. Pour `ordersA` déclaré sur l'IDP :

```csharp
api.WithIdentityResource(ordersA, "Authentication:Inbound")
   .WithIdentityClient(workerA, "Integrations:Accounting:OAuth");
```

`WithIdentityResource` produit uniquement :

```json
{
  "Authentication": {
    "Inbound": {
      "Authority": "https://localhost:<port-idp-a>",
      "Audience": "orders-api-a"
    }
  }
}
```

L'API utilise cette section pour valider les tokens entrants. La seconde section configure son rôle de client OAuth lorsqu'elle appelle une autre API.

`WithIdentityResource` écrit les valeurs avec `WithEnvironment` ; contrairement à `WithIdentityClient`, cette méthode ne comporte pas le contrôle explicite `TryAdd` de collision. Utilise toujours des sections distinctes, y compris pour les deux sens d'authentification.

## Ordre de déclaration et erreurs

Termine l'enregistrement du client avant la projection :

```csharp
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");
api.WithIdentityClient(worker, "Outbound:Orders");
```

La projection standard choisit les champs de credentials au moment de son appel. Ajouter une clé après cet appel ne l'ajoute pas rétroactivement à la liste de champs. Les valeurs des champs retenus et les endpoints sont ensuite évalués au démarrage.

| Situation | Résultat |
| --- | --- |
| Section ou nom de destination vide | Erreur d'argument. |
| Projection explicite de `PrivateKeyPem` sur un client sans clé | Erreur à l'évaluation, avec le client et le champ concernés. |
| Projection de `ClientSecret` sur un client JWT | Même contrôle de champ absent. |
| Clé de destination déjà présente pour `WithIdentityClient` | Erreur de conflit. |
| Fournisseur non sain | Consommateur en attente via `WaitFor`. |

Le câblage Aspire direct reste disponible, par exemple `api.WithEnvironment("CUSTOM_PRIVATE_KEY", worker.PrivateKeyPem!)`. Il ne bénéficie pas à lui seul de la normalisation, des contrôles de projection ni de l'ajout automatique de `WaitFor`.

## Transport des secrets

Les credentials générés sont des paramètres Aspire marqués secrets ; les callbacks conservent leurs références jusqu'à la résolution. Le fournisseur reçoit uniquement le JWK public d'un client JWT. Le processus consommateur reçoit la clé privée effective dans son environnement.

Ce transport n'est pas un coffre de secrets de production. Ne journalise pas les options liées ni les variables contenant les credentials. Pour les mêmes clés de configuration dans Kubernetes, consulte [le guide dédié](kubernetes.md).

## Sources et implémentation

- [IdentityProjectionExtensions.cs](../src/Aspire.IdentityServer.Hosting/IdentityProjectionExtensions.cs)
- [Tests de projection](../tests/Aspire.IdentityServer.Tests/ProjectionTests.cs)
- [Configuration ASP.NET Core : providers et environnement](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0)
