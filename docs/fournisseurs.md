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

`openid`, `profile` et `email` sont enregistrés comme ressources d'identité. Les utilisateurs de cette version possèdent un claim `name`. Demander `email` ne crée pas d'adresse e-mail : aucun paramétrage de claims personnalisés n'est exposé.

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

L'audience d'une assertion `private_key_jwt` est distincte : nos exemples utilisent le `token_endpoint` découvert chez l'IDP. Voir [les assertions](clients.md).

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

`IdentityServerOptions` expose une seule option : `RuntimeDirectory`.

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

Les listes serveur utilisent des indices. La projection de scopes vers un consommateur utilise une chaîne séparée par des espaces.

Aspire configure aussi le port HTTPS de Kestrel et le certificat. `ASPNETCORE_ENVIRONMENT` reprend l'environnement de l'AppHost, que `AddIdentityServer()` limite à `Development` ou `Testing` ; le runtime le vérifie à nouveau et exige un issuer HTTPS loopback. Les réglages fixes figurent dans [la référence](reference.md).
