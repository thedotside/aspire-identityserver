# Référence du paramétrage

[Documentation](README.md) · [Exemples clients](clients.md) · [Projections](projections.md)

Les packages NuGet se nomment `TheDotSide.Aspire.IdentityServer.Hosting`, `TheDotSide.Aspire.IdentityServer.Runtime` et `TheDotSide.Aspire.IdentityServer.Client`. Le préfixe `TheDotSide.` concerne les identifiants NuGet ; les namespaces C# restent `Aspire.IdentityServer.Hosting` pour l'hébergement et `Aspire.IdentityServer.Client` pour les options de consommation et le helper d'assertion.

## Fournisseur et utilisateurs

| Méthode | Retour | Paramètres et défauts |
| --- | --- | --- |
| `builder.AddIdentityServer(string name, Action<IdentityServerOptions>? configure = null)` | `IResourceBuilder<IdentityServerResource>` | Nom de ressource Aspire unique. Callback facultatif pour le dossier runtime. |
| `idp.WithTestUser(string username, string password, string? displayName = null, string? subjectId = null)` | Builder du fournisseur. | Username/password requis ; display name = username si null ; subject = `{resource-name}:{username}` si null. |
| `idp.AddApiResource(string audience)` | `IdentityApiResource` | Audience non vide, unique dans le fournisseur. |
| `apiResource.WithScopes(params string[] scopes)` | Même ressource API. | Ajout cumulatif ; au moins un scope au démarrage. Scopes d'identité et `offline_access` réservés. |

### Options et propriétés

| Type / propriété | Type de valeur | Rôle |
| --- | --- | --- |
| `IdentityServerOptions.RuntimeDirectory` | `string` modifiable. | Dossier contenant le serveur ; défaut `AppContext.BaseDirectory/identityserver`. Chemin relatif résolu depuis l'AppHost. |
| `IdentityServerResource.RegisteredClients` | `IReadOnlyList<IdentityClient>` | Consultation des enregistrements ; utilisée par les tests. |
| `IdentityApiResource.Server` | Builder du fournisseur. | Fournisseur associé. |
| `IdentityApiResource.Audience` | `string` | Audience de la ressource. |
| `IdentityApiResource.Authority` | `EndpointReference` | Endpoint `https` différé. |
| `IdentityApiResource.Scopes` | `IReadOnlyCollection<string>` | Ensemble des scopes de la ressource. |

`IdentityServerResource` dérive d'`ExecutableResource` et démarre `dotnet`. Les options Aspire héritées restent utilisables, sous réserve des contraintes locales du runtime. Il n'existe pas d'option de conteneur ou de déploiement dans cette intégration.

## Création des clients

| Méthode | Retour | Grant |
| --- | --- | --- |
| `idp.AddAuthorizationCodeClient(string clientId)` | `IdentityClient` | `authorization_code`. |
| `idp.AddClientCredentialsClient(string clientId)` | `IdentityClient` | `client_credentials`. |

Le client ID doit être non vide et unique dans son fournisseur. Un enregistrement ne combine pas les deux grants.

## Configuration d'un IdentityClient

Chaque méthode retourne le même `IdentityClient`.

| Méthode | Effet / défaut |
| --- | --- |
| `WithScopes(params string[] scopes)` | Ajoute les scopes autorisés, cumul et déduplication exacte. Aucune valeur par défaut. |
| `WithTokenExchangeFrom(IdentityClient subjectClient, IdentityApiResource api, params string[] userClaims)` | Depuis `0.1.0-alpha.2` : active l'échange pour un client machine, une source Authorization Code et une API du même IdP. `sub` conservé ; autres claims explicitement sélectionnés. Voir [Token Exchange](token-exchange.md). |
| `RequirePkce(bool required = true)` | Clients authorization code uniquement. `UsePkce` vaut déjà `true` par défaut. |
| `WithGeneratedClientSecret()` | Secret aléatoire de 32 octets en Base64, une fois à l'enregistrement. |
| `WithClientSecret(IResourceBuilder<ParameterResource> secret)` | Référence de paramètre obligatoirement secret. |
| `WithGeneratedPrivateKeyJwt()` | RSA 2048 bits ; paramètre privé, JWK public et `kid` calculés. |
| `WithGeneratedBase64PrivateKeyJwt()` | Depuis `0.1.0-alpha.2` : RSA 2048 bits ; paramètre secret `Base64(UTF8(JSON JWK privée))`, JWK publique RS256 et `kid`. |
| `WithBase64PrivateKeyJwt(string privateKeyBase64)` | Depuis `0.1.0-alpha.2` : importe une JWK RSA privée complète >= 2048 bits ; conserve son `kid`, dérive la partie publique. |
| `WithBase64PrivateKeyJwt(IResourceBuilder<ParameterResource> privateKey, string publicJwk)` | Depuis `0.1.0-alpha.2` : paramètre secret différé et JWK publique correspondante ; seule la partie publique est enregistrée dans l'IdP. |
| `WithPrivateKeyJwt(string privateKeyPem)` | Importe une clé privée RSA PEM >= 2048 bits ; calcule JWK et `kid`. |
| `WithPrivateKeyJwt(IResourceBuilder<ParameterResource> privateKey, string publicJwk)` | Paramètre secret différé et JWK RSA public >= 2048 bits avec `kid`. La paire doit correspondre ; pas de comparaison privée/publique pendant l'enregistrement. |
| `WithRedirectUri(EndpointReference endpoint, string path)` | Ajoute un callback de connexion, client interactif uniquement. |
| `WithPostLogoutRedirectUri(EndpointReference endpoint, string path)` | Ajoute un callback de déconnexion, client interactif uniquement. |

L'authentification commence à `none`. Les méthodes de secret la fixent à `client_secret_post`, celles de clé à `private_key_jwt`. Une méthode déjà définie ne peut pas être remplacée. Un client machine laissé à `none` est refusé.

### Propriétés accessibles

| Propriété | Type | Sens |
| --- | --- | --- |
| `Server` | `IResourceBuilder<IdentityServerResource>` | Fournisseur du client. |
| `ClientId` | `string` | Identifiant enregistré. |
| `Authority` | `EndpointReference` | Endpoint HTTPS du fournisseur. |
| `AuthenticationMethod` | `string` | `none`, `client_secret_post`, `private_key_jwt`. |
| `ClientSecret` | `IResourceBuilder<ParameterResource>?` | Référence de secret, sinon null. |
| `PrivateKeyPem` | `IResourceBuilder<ParameterResource>?` | Référence de clé privée, sinon null. |
| `PrivateKeyBase64` | `IResourceBuilder<ParameterResource>?` | Depuis `0.1.0-alpha.2` : référence secrète de JWK privée Base64, exclusive avec PEM. |
| `KeyId` | `string?` | `kid` pour un client JWT. |
| `PublicJwk` | `string?` | JWK public enregistré côté IDP. |
| `UsePkce` | `bool` | Vrai par défaut ; pertinent pour authorization code. |
| `Scopes` | `IReadOnlyCollection<string>` | Scopes autorisés. |
| `TokenExchangeAudience` | `string?` | Depuis `0.1.0-alpha.2` : audience unique des règles d'échange, sinon null. |

Ces propriétés sont consultables ; les enregistrements se modifient par les méthodes fluides. Les URI de redirection et le grant ne sont pas exposés comme propriétés publiques par cette version.

## Projections

Les extensions retournent le builder du consommateur et permettent le chaînage. `T` doit implémenter `IResourceWithEnvironment` et `IResourceWithWaitSupport`.

| Signature | Contenu |
| --- | --- |
| `consumer.WithIdentityClient(client, string section)` | Authority, ID, méthode, scopes et credentials applicables sous la section. |
| `consumer.WithIdentityClient(client, Action<IdentityClientProjection> configure)` | Seulement les champs explicitement sélectionnés. |
| `consumer.WithIdentityResource(apiResource, string section)` | Authority et audience de validation entrante. |

`IdentityClientProjection` expose les méthodes suivantes, prenant chacune une clé `string` et retournant la projection : `Authority`, `ClientId`, `AuthenticationMethod`, `Scopes`, `ClientSecret`, `PrivateKeyPem`, `KeyId` ainsi que `PrivateKeyBase64` et `TokenExchangeAudience`.

Les chemins `:` sont convertis en `__`. Les méthodes de projection ajoutent `WaitFor` vers l'IDP. Les champs exacts, les collisions et l'ordre de déclaration sont détaillés dans [le contrat des projections](projections.md).

## Options du consommateur

`IdentityClientOptions` est un objet de configuration simple avec des propriétés publiques modifiables :

| Propriété | Type | Défaut |
| --- | --- | --- |
| `Authority` | `string` | `""`. |
| `ClientId` | `string` | `""`. |
| `AuthenticationMethod` | `string` | `"none"`. |
| `ClientSecret` | `string?` | `null`. |
| `PrivateKeyPem` | `string?` | `null`. |
| `PrivateKeyBase64` | `string?` | Depuis `0.1.0-alpha.2` : `null` ; `Base64(UTF8(JSON JWK privée))`, exclusif avec PEM. |
| `KeyId` | `string?` | `null`. |
| `Scopes` | `string` | `""`, scopes séparés par des espaces. |
| `TokenExchangeAudience` | `string?` | Depuis `0.1.0-alpha.2` : `null` ; renseigné par la projection standard si l'audience d'échange est unique. |

Le binding ne valide pas automatiquement les champs et ne configure aucun handler. Le helper vérifie les éléments dont il a besoin au moment de signer.

## Helper d'assertion

| Membre | Comportement |
| --- | --- |
| `PrivateKeyJwt.AssertionType` | `urn:ietf:params:oauth:client-assertion-type:jwt-bearer`. |
| `PrivateKeyJwt.CreateAssertion(IdentityClientOptions client, string audience)` | Retourne un JWT signé RS256 ; exige client ID, clé privée RSA >= 2048 bits, `kid` et audience URI absolue. |

Le JWT dure une minute, avec un nouveau `jti` à chaque appel. Le helper d'assertion n'envoie pas de requête HTTP. Voir [clients](clients.md) et [intégration](integration.md).

Avec `PrivateKeyBase64`, le helper signe directement avec les primitives JWK IdentityModel. Il utilise le `kid` de la JWK et refuse un `KeyId` configuré différent. Les erreurs d'import sont fixes et sans exception interne conservée. Voir le [contrat complet et la migration du template](jwk-base64.md).

La version `0.1.0-alpha.2` ajoute `TokenExchange.CreateRequest(IdentityClientOptions client, string tokenEndpoint, string subjectToken, string? audience = null, string? scope = null)`. Il retourne un `HttpRequestMessage` contenant un formulaire RFC 8693 et des credentials neufs. L'appelant possède la requête et l'envoie lui-même. Les constantes `TokenExchange.GrantType` et `TokenExchange.AccessTokenType` exposent les identifiants standard. Aucun cache de tokens ou mécanisme de renouvellement n'est fourni. Voir le [guide](token-exchange.md).

### HttpClient nommé et handler d'échange

`IHttpClientBuilder.AddTokenExchangeHandler(IConfigurationSection clientConfiguration, Action<TokenExchangeHandlerOptions>? configure = null)` active l'échange uniquement sur le client nommé sélectionné. La section est bindée sur des options `IdentityClientOptions` nommées comme ce client.

| Réglage | Comportement |
| --- | --- |
| `TokenExchangeHandlerOptions.SubjectTokenProvider` | Callback `Func<IServiceProvider, HttpRequestMessage, CancellationToken, ValueTask<string?>>`, appelé pour chaque requête. Sans callback, le jeton vient de `request.Options`. |
| `TokenExchange.SubjectTokenKey` | Clé `HttpRequestOptionsKey<string>` permettant de fournir le jeton utilisateur à une requête. |
| `TokenExchangeHandlerOptions.Audience` | Audience explicite ; sinon audience projetée. |
| `TokenExchangeHandlerOptions.Scope` | Scopes explicites ; sinon scopes projetés. |
| `TokenExchangeHttpClientExtensions.BackchannelClientName` | Client nommé distinct pour discovery et token endpoint ; aucun handler d'échange sur ce client. |

Pas de cache de jeton utilisateur, de contexte HTTP ou de token échangé dans le handler partagé. Un jeton absent, une adresse non HTTPS, un header Authorization préexistant ou une réponse d'échange invalide provoquent un échec. Voir le [sample et le guide d'intégration](token-exchange.md#appeler-lapi-avec-un-httpclient-nommé).

## Réglages fixes du runtime

Ces valeurs sont celles du code actuel, sans option fluide associée :

| Réglage | Valeur / comportement |
| --- | --- |
| Environnement accepté | Celui de l'AppHost, `Development` ou `Testing` ; `AddIdentityServer()` refuse les autres et le runtime les refuse aussi. Un avertissement est journalisé à chaque démarrage. |
| Issuer | URI absolue HTTPS loopback exigée. |
| Écoute | HTTPS sur `localhost` uniquement, au port `ASPNETCORE_HTTPS_PORTS` fixé par Aspire ; `Kestrel:Endpoints` refusé et adresses effectives vérifiées au démarrage. |
| Token d'accès | Durée configurée de 300 secondes ; Token Exchange borné par la validité restante du jeton source. |
| Consentement | `RequireConsent = false`. |
| Claims utilisateur dans l'ID token | `AlwaysIncludeUserClaimsInIdToken = true`, sous réserve des claims effectivement disponibles et scopes demandés. |
| Gestion automatique des clés Duende | Désactivée ; clé de développement en mémoire, `persistKey: false`. |
| Protection des données | Fournisseur éphémère, application `AspireIdentityServer:{name}`. |
| Stockages | En mémoire. |
| Health check | `/health`. |
| Interface | Razor Pages `/Account/Login`, `/Account/Logout`, `/Error`. |
| Racine `/` | Redirection vers `/.well-known/openid-configuration`. |

Les mécanismes généraux de Duende ne deviennent pas des fonctionnalités configurables de ce package du seul fait de sa dépendance. Aucun paramètre public n'est exposé ici pour refresh tokens, fédération, CORS, claims personnalisés, persistance ou rotation durable de clés.

## Sources de référence

- [Extensions du fournisseur](../src/Aspire.IdentityServer.Hosting/IdentityServerExtensions.cs).
- [Ressource et options](../src/Aspire.IdentityServer.Hosting/IdentityServerResource.cs).
- [IdentityClient](../src/Aspire.IdentityServer.Hosting/IdentityClient.cs).
- [Projections](../src/Aspire.IdentityServer.Hosting/IdentityProjectionExtensions.cs).
- [Runtime](../src/Aspire.IdentityServer.Server/Program.cs).
- [Options client](../src/Aspire.IdentityServer.Client/IdentityClientOptions.cs) et [helper](../src/Aspire.IdentityServer.Client/PrivateKeyJwt.cs).
