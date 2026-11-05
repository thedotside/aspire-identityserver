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

Les scopes d'identité sélectionnent les claims de l'ID token et de UserInfo (`profile` pour `given_name` et `family_name`, `email` pour `email`, ou un scope déclaré ci-dessous). Avec `AlwaysIncludeUserClaimsInIdToken`, ces claims figurent dans l'ID token. Les access tokens utilisent une sélection distincte : les [claims des ressources API](#claims-utilisateur-dans-les-access-tokens), ou la liste explicite d'un [Token Exchange](token-exchange.md).

## Scopes d'identité personnalisés

```csharp
var idp = builder.AddIdentityServer("idp")
    .WithIdentityScope("teams", "team");
```

Un client interactif qui déclare et demande `teams` reçoit les claims `team` de l'utilisateur. Le nom suit les règles d'un scope ; `openid`, `profile`, `email` et `offline_access` sont réservés, et un même nom ne peut pas être à la fois scope d'identité et scope d'API. Il faut au moins un claim utilisateur, jamais un claim de protocole. Un client machine ne peut pas demander de scope d'identité.

## Claims utilisateur dans les access tokens

Depuis `0.1.0-alpha.5` (en développement), `api.WithUserClaims(...)` déclare les types de claims à inclure dans les access tokens destinés à une ressource API. Les valeurs proviennent du profil de l'utilisateur authentifié.

### Exemple : `communities` et `member_of` pour Xp

Dans cet AppHost, `web` est le projet du client OIDC déjà ajouté à `builder`, avec un endpoint HTTPS. Le mot de passe de développement est fourni par la configuration locale ; sa projection vers le fournisseur reste un paramètre secret Aspire.

```csharp
var idp = builder.AddIdentityServer("idp")
    .WithIdentityScope("communities", "member_of");

var password = builder.Configuration["DevelopmentUser:Password"]
    ?? throw new InvalidOperationException("Configure DevelopmentUser:Password locally.");
idp.AddTestUser("developer", password, "Developer")
    .WithClaim("member_of", "product.readers", "product.contributors");

var xp = idp.AddApiResource("xp")
    .WithScopes("xp")
    .WithUserClaims("member_of");

var frontend = idp.AddAuthorizationCodeClient("frontend")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("openid", "communities", "xp")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc");
```

Le client doit effectivement demander `scope=openid communities xp` à la connexion. `WithScopes` autorise ces scopes ; il ne remplace pas la requête OIDC du client.

| Configuration et requête | ID token et UserInfo | Access token |
| --- | --- | --- |
| `communities` demandé, sans scope d'API Xp | `member_of` disponible | Pas de `member_of` issu de Xp ; pas d'audience `xp`. |
| `communities` et `xp` demandés | `member_of` disponible | Audience `xp` et `member_of` disponible. |
| `xp` demandé sans `communities` | Pas de `member_of` via ce scope d'identité | Audience `xp` et `member_of` disponible. |
| Un scope d'une autre API, sans déclaration de ce claim | Selon les scopes d'identité demandés | Pas de `member_of` issu de Xp. |

« Disponible » signifie que le profil possède la valeur : déclarer un type de claim ne crée aucune appartenance. Un utilisateur sans `member_of` conserve un access token sans ce claim. Plusieurs valeurs sont un tableau JSON ; une seule valeur est une chaîne.

`WithUserClaims` s'applique à **tous les scopes de la ressource API**, comme `ApiResource.UserClaims` dans Duende. Il ne conditionne pas l'émission à `communities`. `WithIdentityScope("communities", "member_of")` sélectionne seulement l'ID token et UserInfo ; le scope d'API `xp` sélectionne l'audience et les claims de cette ressource. Les noms des scopes d'identité et d'API restent distincts.

Les types doivent être des claims utilisateur, sans espace ni caractère de contrôle ; les claims de protocole (`sub`, `aud`, `scope`, `exp`, `act`, etc.) sont refusés. Les appels sont cumulatifs, les doublons exacts ignorés, et il faut au moins un type. Sans `WithUserClaims`, une ressource API n'ajoute aucun claim de profil. Si plusieurs API sont sélectionnées, leurs claims sont réunis dans le même access token : un token à plusieurs audiences n'isole pas ses claims par destinataire.

Un token `client_credentials` n'a pas de profil utilisateur : cette déclaration ne lui ajoute pas `member_of`. Le Token Exchange conserve sa propre liste `userClaims`, même si l'API déclare d'autres types. Le renouvellement conserve les claims de l'access token selon les réglages de refresh de Duende ; il ne recharge pas automatiquement les appartenances modifiées depuis la connexion.

Pour le relais direct BFF → Xp, le BFF transmet l'access token de connexion. Xp valide signature, issuer, audience et expiration puis applique sa policy `member_of`. Avec un utilisateur autorisé, le sample vérifie une réponse 200 ; sans appartenance, la même policy renvoie 403. Aucun échange n'est nécessaire pour ce relais.

### Migration depuis alpha.4

En alpha.4, `WithIdentityScope("communities", "member_of")` alimente l'ID token et UserInfo, mais ne suffit pas pour l'access token Xp. Après installation d'une version contenant cet ajout, déclarer `.WithUserClaims("member_of")` sur la ressource API Xp et demander son scope d'API en plus de `communities`. Refaire la connexion pour obtenir un nouveau token ; un JWT déjà émis ne change pas. Conserver la policy d'autorisation de Xp.

Le contrat Hosting/Runtime passe à **4** : utiliser des versions de packages correspondantes. La version alpha.5 est en développement ; cet exemple ne décrit pas le package alpha.4 déjà publié.

Voir les références Duende : [émission des claims](https://docs.duendesoftware.com/identityserver/fundamentals/claims/) et [claims des ressources API](https://docs.duendesoftware.com/identityserver/fundamentals/resources/api-resources/#adding-user-claims).

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
| `IdentityServer__Apis__{i}__UserClaims__{j}` | Type de claim utilisateur sélectionné pour les access tokens de cette ressource (contrat 4). |
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
