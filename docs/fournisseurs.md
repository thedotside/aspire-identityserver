# Fournisseurs, utilisateurs et ressources API

[Documentation](README.md) · [Clients](clients.md) · [Référence](reference.md)

## Déclarer un fournisseur

Dans un AppHost où `builder` est le résultat de `DistributedApplication.CreateBuilder(args)` :

```csharp
var idpA = builder.AddIdentityServer("idp-a")
    .WithTestUser("alice", "alice", "Alice A");
var idpB = builder.AddIdentityServer("idp-b")
    .WithTestUser("alice", "alice", "Alice B");
```

Chaque appel crée un processus, un endpoint HTTPS et un issuer indépendants. Les noms de ressources suivent les règles Aspire et doivent être uniques dans l'AppHost.

Aspire alloue les ports. `idpA.GetEndpoint("https")` et `client.Authority` sont des références différées, résolues lorsque les endpoints sont alloués. N'inscris pas de port fixe dans les consommateurs.

La ressource expose un health check `/health` via HTTPS. Les projections ajoutent automatiquement `WaitFor` vers le fournisseur.

## Isolation et durée de vie

| Élément | Comportement |
| --- | --- |
| Clients, ressources API, utilisateurs | Déclarés dans l'AppHost, chargés en mémoire au démarrage du serveur. |
| Clé de signature des tokens | Propre au fournisseur, recréée à chaque démarrage de son processus. |
| Grants, codes et sessions serveur | En mémoire, perdus au redémarrage. |
| Protection des cookies | Data Protection éphémère, isolée par nom de fournisseur. |
| Cookies du fournisseur | `aspire-idp-{name}`, suffixes `.csrf` et `.session`. |
| Clé ou secret client généré | Créé lors de l'enregistrement et conservé pour cette instance de l'AppHost. |

Redémarrer le seul processus IDP renouvelle sa clé de signature et son état éphémère. Redémarrer l'AppHost régénère aussi les credentials clients automatiques. Une clé fournie reste stable si sa source reste stable.

La clé de signature de l'IDP signe les tokens. La clé privée d'un client `private_key_jwt` authentifie ce client auprès de l'IDP. Ce sont des clés distinctes.

## Utilisateurs de test

```csharp
idpA.WithTestUser(
    username: "bob",
    password: "bob-dev-only",
    displayName: "Bob local",
    subjectId: "person-bob");
```

| Paramètre | Obligatoire | Défaut |
| --- | --- | --- |
| `username` | Oui, non vide. | Aucun. |
| `password` | Oui, non vide. | Aucun. |
| `displayName` | Non. | `username` si `null`. |
| `subjectId` | Non. | `{nom-fournisseur}:{username}` si `null`. |

Nom d'utilisateur et subject ID doivent être uniques dans un fournisseur. Deux fournisseurs peuvent utiliser les mêmes noms d'utilisateurs ou client IDs.

Le mot de passe est un paramètre secret Aspire. Le serveur emploie les utilisateurs de test Duende, sans gestion de comptes persistants.

`openid`, `profile` et `email` sont enregistrés comme ressources d'identité. Chaque utilisateur possède un claim `name`, issu du nom affiché.

### Claims de profil

Depuis `0.1.0-alpha.4`, `AddTestUser` prend les mêmes paramètres que `WithTestUser` et retourne l'utilisateur, pour lui ajouter des claims :

```csharp
idpA.AddTestUser("alice", "alice-dev-only", "Alice A")
    .WithClaim("email", "alice@example.test")
    .WithClaim("given_name", "Alice")
    .WithClaim("family_name", "Anderson")
    .WithClaim("team", "sales", "support");
```

Plusieurs valeurs d'un même type sont émises sous forme de tableau JSON, une seule valeur sous forme de chaîne : un consommateur doit accepter les deux. Les claims de protocole (`sub`, `iss`, `aud`, `scope`, `act`...) et `name`, issu du nom affiché, sont refusés.

Les scopes d'identité sélectionnent les claims de l'ID token et de UserInfo (`profile` pour `given_name` et `family_name`, `email` pour `email`, ou un scope déclaré ci-dessous). Avec `AlwaysIncludeUserClaimsInIdToken`, ces claims figurent dans l'ID token. Depuis alpha.5, ces mêmes scopes accordés sélectionnent les claims de l'[access token de connexion](#claims-des-scopes-didentité-dans-laccess-token). Un [Token Exchange](token-exchange.md) garde sa liste explicite de claims.

## Scopes d'identité personnalisés

```csharp
var idp = builder.AddIdentityServer("idp")
    .WithIdentityScope("teams", "team");
```

Un client interactif qui déclare et demande `teams` reçoit les claims `team` de l'utilisateur. Le nom suit les règles d'un scope ; `openid`, `profile`, `email` et `offline_access` sont réservés, et un même nom ne peut pas être à la fois scope d'identité et scope d'API. Il faut au moins un claim utilisateur, jamais un claim de protocole. Un client machine ne peut pas demander de scope d'identité.

## Claims des scopes d'identité dans l'access token

Depuis `0.1.0-alpha.5`, les scopes d'identité effectivement accordés à un client **Authorization Code** sélectionnent leurs claims dans l'access token de connexion, en plus de l'ID token et UserInfo. Le fournisseur étend ainsi la sélection par défaut de Duende : le profile service filtre les valeurs du profil à partir des seules ressources d'identité accordées, sans recopier tous les claims de l'utilisateur.

### BFF et YARP vers Xp

Dans cet exemple, `web` est le projet BFF déjà ajouté à `builder`, avec un endpoint HTTPS. Son mot de passe de développement est lu dans la configuration locale puis projeté comme paramètre secret Aspire.

```csharp
var idp = builder.AddIdentityServer("idp")
    .WithIdentityScope("communities", "member_of");

var password = builder.Configuration["DevelopmentUser:Password"]
    ?? throw new InvalidOperationException("Configure DevelopmentUser:Password locally.");
idp.AddTestUser("developer", password, "Developer")
    .WithClaim("member_of", "product.readers", "product.contributors");

var xp = idp.AddApiResource("xp").WithScopes("xp");
var bff = idp.AddAuthorizationCodeClient("bff")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("openid", "communities", "xp")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc");
```

Le client OIDC du BFF demande réellement `scope=openid communities xp`. `WithScopes` déclare les scopes autorisés ; il ne remplace pas cette requête. `communities` est un **scope d'identité du client interactif**, jamais un scope de la ressource API ni du client technique. `xp` désigne ici la ressource API qui donne l'audience `xp` à l'access token.

Après la connexion, l'ID token, UserInfo et l'access token contiennent les appartenances `member_of` du profil. YARP transmet cet access token directement à Xp. Xp valide sa signature, son issuer, son audience et son expiration, puis applique la policy `member_of` du produit. Ce parcours ne nécessite pas de token exchange. La SPA n'a pas besoin de recevoir les tokens du BFF.

| Scopes réellement demandés par le BFF | `member_of` dans ID token / UserInfo | `member_of` dans l'access token | Audience API |
| --- | --- | --- | --- |
| `openid communities xp` | Oui, si présent dans le profil | Oui, si présent dans le profil | `xp` |
| `openid xp` | Non | Non | `xp` |
| `openid communities` | Oui, si présent dans le profil | Oui, si présent dans le profil | Aucune audience API |

Un scope seulement autorisé mais non demandé n'ajoute pas ses claims. Déclarer `communities` ne crée aucune appartenance : un utilisateur sans `member_of` conserve un token sans ce claim. Plusieurs valeurs sont émises sous forme de tableau JSON, une valeur sous forme de chaîne. Les autres champs du profil restent filtrés selon les scopes accordés ; les claims de protocole ne proviennent pas des données utilisateur.

### Sans Xp : token exchange du BFF vers Capa

Le client de connexion demande `openid communities`, sans scope d'API Capa. Son access token contient les appartenances, mais n'est pas destiné à Capa. Un enregistrement technique distinct du BFF porte les scopes de Capa et une autorisation explicite d'échange :

```csharp
var session = idp.AddAuthorizationCodeClient("bff-session")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("openid", "communities")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc");

var capa = idp.AddApiResource("capa").WithScopes("capa.read");
var bffCapa = idp.AddClientCredentialsClient("bff-capa")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("capa.read")
    .WithTokenExchangeFrom(
        session,
        capa,
        TokenExchangeScopes.FromMachineClient,
        userClaims: ["member_of"]
    );
```

Pour appeler Capa au nom de l'utilisateur, le BFF présente son access token de connexion comme `subject_token` et demande `audience=capa&scope=capa.read`. Le mode `FromMachineClient` autorise ce scope explicitement demandé même s'il est absent du token utilisateur. Le token émis cible Capa, conserve `sub`, indique l'acteur technique `act` et contient uniquement les claims utilisateur sélectionnés par la policy d'échange. `communities` ne figure pas dans les scopes de la requête d'échange ni dans ceux du client technique. Voir le [guide Token Exchange](token-exchange.md).

### Limites et migration depuis alpha.4

Les clients `client_credentials` ne peuvent pas demander de scope d'identité. Un appel technique ne gagne aucun claim utilisateur ; demander `communities` avec ce grant est refusé. L'AppHost refuse également qu'un même nom soit déclaré comme scope d'identité et scope d'API.

Les claims liés aux scopes accordés accompagnent le token de connexion indépendamment de son audience. Un token contenant `member_of` reste refusé par une API dont l'audience ne correspond pas. Les tests réels vérifient 200 avec l'appartenance attendue, 403 avec la bonne audience mais sans appartenance, et 401 avec une autre audience. Sans Xp, ils vérifient aussi le refus du relais direct vers Capa puis le succès de l'échange explicite.

En alpha.4, `WithIdentityScope("communities", "member_of")` alimentait seulement l'ID token et UserInfo. L'alpha.5 étend l'émission à l'access token Authorization Code, sans nouvelle déclaration sur la ressource API et sans changement de contrat de configuration (version **3**). Mettre à jour les packages correspondants puis refaire la connexion pour obtenir un nouveau token ; un JWT déjà émis ne change pas.

Le refresh token conserve les claims de l'access token selon les réglages Duende existants ; il ne recharge pas automatiquement les appartenances modifiées après la connexion. Le Token Exchange conserve sa liste explicite `userClaims` et ne propage pas automatiquement tous les claims du token source.

## MFA simulée

Une application qui demande un niveau d'authentification par `acr_values`, par exemple une MFA ou un appareil de confiance, le reçoit aussi en local. Le fournisseur simule le second facteur avec un code fixe déclaré dans l'AppHost :

```csharp
var idp = builder.AddIdentityServer("idp")
    .WithSimulatedMfa("111111", "urn:exemple:loa:mfa");
```

- Une demande d'autorisation qui contient un niveau déclaré dans `acr_values` passe par une page de code : après le mot de passe, ou directement si l'utilisateur a déjà une session sans ce niveau.
- Après le bon code, l'ID token et l'access token portent `acr` (le niveau demandé) et `amr` (`pwd`, `otp` et `mfa`).
- La session garde ce niveau jusqu'à la déconnexion : redemander le même niveau ne redemande pas le code. Un autre niveau déclaré le redemande.
- **Cancel** renvoie `access_denied` au client, comme sur la page de connexion. Avec `prompt=none`, une session sans le niveau reçoit `interaction_required`.
- Les valeurs non déclarées sont ignorées, avec un avertissement dans les logs du fournisseur. Les préfixes `idp:` et `tenant:` restent réservés à Duende.
- La découverte publie les niveaux dans `acr_values_supported`. La commande **Show registrations** du dashboard affiche le code et les niveaux.

Le code n'est pas un secret. C'est une valeur fixe de développement, de 4 à 10 chiffres, déclarée en un seul appel avec tous les niveaux. Aucun jeton ne porte `acr` sans que le code ait été saisi.

Côté client ASP.NET Core, le handler OpenID Connect retire `acr` de l'utilisateur connecté par défaut. Pour vérifier le niveau à partir des claims, retire cette action :

```csharp
options.AdditionalAuthorizationParameters["acr_values"] = "urn:exemple:loa:mfa";
options.ClaimActions.Remove("acr");
```

L'exemple déclare `urn:sample:loa:mfa` et `urn:sample:loa:high` sur `idp-a`, avec le code `111111`. Le schéma `mfa` du site web demande le premier niveau.

## Refresh tokens

Un client interactif qui ajoute `offline_access` à ses scopes, et le demande à la connexion, reçoit un refresh token. Le token d'accès renouvelé conserve le sujet, l'heure et la méthode d'authentification : il reste utilisable comme jeton source d'un Token Exchange. Les refresh tokens suivent les réglages par défaut de Duende et restent en mémoire, comme le reste de l'état du fournisseur. Un client machine ne peut pas demander `offline_access`.

## Scopes et audience

```csharp
var ordersA = idpA.AddApiResource("orders-api-a")
    .WithScopes("orders.read", "orders.write");

var workerA = idpA.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");
```

Trois niveaux interviennent :

1. La ressource déclare les scopes disponibles et leur audience : `orders-api-a`.
2. Le client déclare les scopes qu'il peut demander : ici `orders.read`.
3. La requête de token indique ceux effectivement demandés : `scope=orders.read`.

Une demande valide de ce client pour `orders.read` produit un token avec ce scope et l'audience `orders-api-a`. `orders.write` n'est pas accordé.

Ajouter un scope au client ne le déclare pas comme scope d'API. Il faut l'associer à une ressource du même fournisseur. Un client machine ne peut pas demander `openid`, `profile` ou `email`.

Il n'existe pas de `WithAudience()` sur le client. L'audience du token d'accès découle des ressources associées aux scopes demandés. Plusieurs ressources peuvent conduire à plusieurs audiences. Évite de partager un scope entre plusieurs ressources si tu souhaites une audience unique.

L'audience d'une assertion `private_key_jwt` est distincte : c'est l'issuer de l'IDP, comme le demande draft-ietf-oauth-rfc7523bis. Voir [les assertions](clients.md#assertions-private_key_jwt).

## Règles des ressources

- Audience obligatoire et unique dans le fournisseur.
- Au moins un scope par ressource avant le démarrage.
- `WithScopes()` ajoute des valeurs ; plusieurs appels cumulent les scopes et suppriment les doublons exacts.
- Les scopes sont sensibles à la casse.
- Chaque argument est un scope unique : `WithScopes("orders.read", "orders.write")`.
- `openid`, `profile`, `email` et `offline_access` sont refusés comme scopes de ressource API.
- Espaces, guillemets, antislashs et caractères hors ASCII autorisé sont refusés dans un nom de scope.

Une API doit valider issuer, signature, expiration et audience, puis vérifier le scope de l'opération. Voir [l'intégration API](integration.md).

## Emplacement du runtime

`IdentityServerOptions` expose `RuntimeDirectory` et `AcceptTokenEndpointAudience` ; cette dernière est décrite avec [les assertions](clients.md#assertions-private_key_jwt).

```csharp
var idp = builder.AddIdentityServer("idp", options =>
{
    options.RuntimeDirectory = "chemin/vers/runtime-local";
});
```

Défaut : `Path.Combine(AppContext.BaseDirectory, "identityserver")`. Un chemin relatif est résolu depuis `builder.AppHostDirectory`. Le répertoire doit contenir `Aspire.IdentityServer.Server.dll`, ses dépendances et fichiers de runtime. La présence de l'assembly est contrôlée pendant `AddIdentityServer()`.

Cette option sert au développement du dépôt. Elle ne rend pas le fournisseur déployable.

## Configuration interne du serveur

L'AppHost transmet la configuration par environnement. Ces clés expliquent l'implémentation ; privilégie l'API fluide pour paramétrer l'IDP.

| Préfixe ou clé | Contenu |
| --- | --- |
| `IdentityServer__Name` | Nom de ressource. |
| `IdentityServer__Issuer` | Endpoint HTTPS local du fournisseur. |
| `IdentityServer__Apis__{i}__Audience` | Audience d'une ressource. |
| `IdentityServer__Apis__{i}__Scopes__{j}` | Scope de cette ressource. |
| `IdentityServer__IdentityScopes__{i}__Name` | Scope d'identité personnalisé. |
| `IdentityServer__IdentityScopes__{i}__UserClaims__{j}` | Claim utilisateur émis pour ce scope. |
| `IdentityServer__Clients__{i}__ClientId` | Identifiant du client. |
| `IdentityServer__Clients__{i}__GrantType` | `authorization_code` ou `client_credentials`. |
| `IdentityServer__Clients__{i}__AuthenticationMethod` | `none`, `client_secret_post` ou `private_key_jwt`. |
| `IdentityServer__Clients__{i}__RequirePkce` | Booléen de l'enregistrement. |
| `IdentityServer__Clients__{i}__ClientSecretHash` | Empreinte `Base64(SHA-256(secret))` d'un client avec secret, calculée au lancement ; jamais le secret lui-même. |
| `IdentityServer__Clients__{i}__PublicJwk` | Clé publique du client JWT ; aucune clé privée client. |
| `IdentityServer__Clients__{i}__Scopes__{j}` | Scope autorisé au client. |
| `IdentityServer__Clients__{i}__RedirectUris__{j}` | Redirection de connexion résolue. |
| `IdentityServer__Clients__{i}__PostLogoutRedirectUris__{j}` | Redirection de déconnexion résolue. |
| `IdentityServer__Users__{i}__Username` | Nom d'utilisateur. |
| `IdentityServer__Users__{i}__SubjectId` | Identifiant de sujet. |
| `IdentityServer__Users__{i}__DisplayName` | Nom affiché. |
| `IdentityServer__Users__{i}__Password` | Paramètre secret de mot de passe. |
| `IdentityServer__Users__{i}__Claims__{j}__Type` et `__Value` | Claim de profil ; un type peut se répéter. |

Les listes serveur utilisent des indices. La projection de scopes vers un consommateur utilise une chaîne séparée par des espaces.

Aspire configure aussi le port HTTPS de Kestrel et le certificat. `ASPNETCORE_ENVIRONMENT` reprend l'environnement de l'AppHost, que `AddIdentityServer()` limite à `Development` ou `Testing` ; le runtime le vérifie à nouveau et exige un issuer HTTPS loopback. Les réglages fixes figurent dans [la référence](reference.md).
