# Référence du paramétrage

[Documentation](README.md) · [Exemples clients](clients.md) · [Projections](projections.md)

Les packages NuGet se nomment `TheDotSide.Aspire.IdentityServer.Hosting`, `TheDotSide.Aspire.IdentityServer.Runtime` et `TheDotSide.Aspire.IdentityServer.Client`. Le préfixe `TheDotSide.` concerne les identifiants NuGet. Comme les intégrations Aspire officielles, l'hébergement place ses extensions dans le namespace `Aspire.Hosting` et ses types de ressource dans `Aspire.Hosting.ApplicationModel` : un AppHost les importe déjà, sans `using` supplémentaire. Les options de consommation et les helpers restent dans `Aspire.IdentityServer.Client`.

Les règles d'enregistrement (scopes déclarés, méthode d'authentification, PKCE, redirections, scopes partagés du Token Exchange) sont vérifiées avant le démarrage de toute ressource : une erreur arrête l'AppHost au lieu d'un seul processus fournisseur.

## Fournisseur et utilisateurs

| Méthode | Retour | Paramètres et défauts |
| --- | --- | --- |
| `builder.AddIdentityServer(string name, Action<IdentityServerOptions>? configure = null)` | `IResourceBuilder<IdentityServerResource>` | Nom de ressource Aspire unique. Callback facultatif pour le dossier runtime. |
| `consumer.WithReference(idp)` | Builder du consommateur. | `IdentityServerResource` est une ressource de découverte de services : le consommateur reçoit `services__{name}__https__0`. |
| `idp.WithTestUser(string username, string password, string? displayName = null, string? subjectId = null)` | Builder du fournisseur. | Username/password requis ; display name = username si null ; subject = `{resource-name}:{username}` si null. |
| `idp.AddTestUser(string username, string password, string? displayName = null, string? subjectId = null)` | `IdentityTestUser` | Depuis `0.1.0-alpha.4` : mêmes règles que `WithTestUser` ; retourne l'utilisateur pour ajouter des claims. |
| `testUser.WithClaim(string type, params string[] values)` | Même utilisateur. | Depuis `0.1.0-alpha.4` : claims de profil, plusieurs valeurs par type, doublons exacts ignorés. Claims de protocole et `name` refusés. |
| `idp.WithIdentityScope(string name, params string[] userClaims)` | Builder du fournisseur. | Depuis `0.1.0-alpha.4` : scope d'identité personnalisé, nom unique, hors `openid`/`profile`/`email`/`offline_access` et scopes d'API ; au moins un claim utilisateur. |
| `idp.AddApiResource(string audience)` | `IdentityApiResource` | Audience non vide, unique dans le fournisseur. |
| `apiResource.WithScopes(params string[] scopes)` | Même ressource API. | Ajout cumulatif ; au moins un scope au démarrage. Scopes d'identité et `offline_access` réservés. |
| `apiResource.WithUserClaims(params string[] userClaims)` | Même ressource API. | Depuis `0.1.0-alpha.5` (en développement) : claims utilisateur des access tokens dès qu'un scope de cette API est accordé. Cumul et déduplication exacte ; au moins un type, aucun claim de protocole. Voir [l'exemple communities/member_of](fournisseurs.md#claims-utilisateur-dans-les-access-tokens). |

### Options et propriétés

| Type / propriété | Type de valeur | Rôle |
| --- | --- | --- |
| `IdentityServerOptions.RuntimeDirectory` | `string` modifiable. | Dossier contenant le serveur ; défaut `AppContext.BaseDirectory/identityserver`. Chemin relatif résolu depuis l'AppHost. |
| `IdentityServerOptions.AcceptTokenEndpointAudience` | `bool`, défaut `false`. | Accepte aussi les assertions RFC 7523 d'origine, dont l'audience est le token endpoint. Par défaut, seul le profil draft-ietf-oauth-rfc7523bis est accepté. |
| `IdentityServerResource.RegisteredClients` | `IReadOnlyList<IdentityClient>` | Consultation des enregistrements ; utilisée par les tests. |
| `IdentityApiResource.Server` | Builder du fournisseur. | Fournisseur associé. |
| `IdentityApiResource.Audience` | `string` | Audience de la ressource. |
| `IdentityApiResource.Authority` | `EndpointReference` | Endpoint `https` différé. |
| `IdentityApiResource.Scopes` | `IReadOnlyCollection<string>` | Ensemble des scopes de la ressource. |
| `IdentityApiResource.UserClaims` | `IReadOnlyCollection<string>` | Types de claims de profil sélectionnés pour les access tokens de la ressource. Vide par défaut. |
| `IdentityTestUser.Username`, `SubjectId`, `DisplayName` | `string` | Identité de l'utilisateur de test. |
| `IdentityTestUser.Password` | `IResourceBuilder<ParameterResource>` | Paramètre secret du mot de passe. |
| `IdentityTestUser.Claims` | `IReadOnlyList<KeyValuePair<string, string>>` | Claims de profil, dans l'ordre de déclaration. |

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
| `WithScopes(params string[] scopes)` | Ajoute les scopes autorisés, cumul et déduplication exacte. Aucune valeur par défaut. Un client interactif peut aussi déclarer les scopes d'identité et `offline_access`. |
| `WithTokenExchangeFrom(IdentityClient subjectClient, IdentityApiResource api, params string[] userClaims)` | Depuis `0.1.0-alpha.2` : active l'échange pour un client machine, une source Authorization Code et une API du même IdP. `sub` conservé ; autres claims explicitement sélectionnés. Voir [Token Exchange](token-exchange.md). |
| `WithTokenExchangeFrom(IdentityClient subjectClient, IdentityApiResource api, TokenExchangeScopes scopes, params string[] userClaims)` | Depuis `0.1.0-alpha.4` : choisit la règle de scopes. `FromSubjectToken`, par défaut, réduit le jeton utilisateur ; `FromMachineClient` accorde les scopes de l'API autorisés au client machine, demandés explicitement. Voir [la délégation](token-exchange.md#scopes-choisis-par-le-client-machine). |
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
| `consumer.WithIdentityApiResource(apiResource, string section)` | Authority et audience de validation entrante. `WithIdentityResource`, l'ancien nom, reste disponible et obsolète : chez Duende, une « identity resource » désigne `openid` ou `profile`. |

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
| `PrivateKeyJwt.ClientAuthenticationType` | `client-authentication+jwt`. |
| `PrivateKeyJwt.CreateAssertion(IdentityClientOptions client, string issuer)` | Retourne un JWT signé RS256 au profil draft-ietf-oauth-rfc7523bis : `typ: client-authentication+jwt`, `aud` = issuer seul. Exige client ID, clé privée RSA >= 2048 bits, `kid` et issuer URI absolue. |
| `PrivateKeyJwt.CreateTokenEndpointAssertion(IdentityClientOptions client, string tokenEndpoint)` | Profil RFC 7523 d'origine, pour un fournisseur qui refuse l'issuer : `typ: JWT`, `aud` = token endpoint. Refusé par le fournisseur local sans `AcceptTokenEndpointAudience`. |

Le JWT dure une minute, avec un nouveau `jti` à chaque appel. Le helper d'assertion n'envoie pas de requête HTTP. Voir [clients](clients.md) et [intégration](integration.md).

Le helper importe la clé une fois par instance d'`IdentityClientOptions`, puis la réutilise ; il la réimporte et la revalide si la clé ou le `KeyId` changent, par exemple au rechargement d'`IOptionsMonitor`. Les composants privés décodés sont effacés de la mémoire après import, et les signatures concurrentes sur une même clé sont sérialisées.

Avec `PrivateKeyBase64`, le helper importe directement les composants validés dans une clé RSA, sans passer le JSON privé à IdentityModel. Il utilise le `kid` de la JWK et refuse un `KeyId` configuré différent. Les erreurs d'import sont fixes et sans exception interne conservée. Voir le [contrat complet et la migration depuis PEM](jwk-base64.md).

`TokenExchange.CreateRequest(IdentityClientOptions client, string tokenEndpoint, string subjectToken, string? audience = null, string? scope = null, string? issuer = null)` crée la requête ; `issuer`, l'audience de l'assertion, vaut par défaut l'authority configurée. Il retourne un `HttpRequestMessage` contenant un formulaire RFC 8693 et des credentials neufs. L'appelant possède la requête et l'envoie lui-même. Les constantes `TokenExchange.GrantType` et `TokenExchange.AccessTokenType` exposent les identifiants standard. Aucun cache de tokens ou mécanisme de renouvellement n'est fourni. Voir le [guide](token-exchange.md).

### HttpClient nommé et handler d'échange

`IHttpClientBuilder.AddTokenExchangeHandler(IConfigurationSection clientConfiguration, Action<TokenExchangeHandlerOptions>? configure = null)` active l'échange uniquement sur le client nommé sélectionné. La section est bindée sur des options `IdentityClientOptions` nommées comme ce client.

| Réglage | Comportement |
| --- | --- |
| `TokenExchangeHandlerOptions.SubjectTokenProvider` | Callback `Func<IServiceProvider, HttpRequestMessage, CancellationToken, ValueTask<string?>>`, appelé pour chaque requête. Sans callback, le jeton vient de `request.Options`. |
| `TokenExchange.SubjectTokenKey` | Clé `HttpRequestOptionsKey<string>` permettant de fournir le jeton utilisateur à une requête. |
| `TokenExchangeHandlerOptions.Audience` | Audience explicite ; sinon audience projetée. |
| `TokenExchangeHandlerOptions.AllowedOrigins` | Origines (schéma, hôte, port) autorisées à recevoir le jeton échangé. Vide par défaut : toute adresse HTTPS du client nommé. |
| `TokenExchangeHandlerOptions.BackchannelTimeout` | Budget de la découverte et de l'échange, tentatives comprises ; 30 s par défaut. Dépassé : `TimeoutException`. |
| `TokenExchangeHandlerOptions.Scope` | Scopes explicites ; sinon scopes projetés. |
| `TokenExchangeHttpClientExtensions.BackchannelClientName` | Client nommé distinct pour discovery et token endpoint ; aucun handler d'échange sur ce client. |

Pas de cache de jeton utilisateur, de contexte HTTP ou de token échangé dans le handler partagé. Un jeton absent, une adresse non HTTPS, un header Authorization préexistant ou une réponse d'échange invalide provoquent un échec. Voir le [sample et le guide d'intégration](token-exchange.md#appeler-lapi-avec-un-httpclient-nommé).

## Réglages fixes du runtime

Ces valeurs sont celles du code actuel, sans option fluide associée :

| Réglage | Valeur / comportement |
| --- | --- |
| Contrat de configuration | `IdentityServer__ContractVersion`, version `4`. Le runtime refuse de démarrer si l'AppHost écrit une autre version : Hosting et Runtime doivent avoir la même version de paquet. |
| Environnement accepté | Celui de l'AppHost, `Development` ou `Testing` ; `AddIdentityServer()` refuse les autres et le runtime les refuse aussi. Un avertissement est journalisé à chaque démarrage. |
| Issuer | URI absolue HTTPS loopback exigée. |
| Écoute | HTTPS sur `localhost` uniquement, au port `ASPNETCORE_HTTPS_PORTS` fixé par Aspire ; `Kestrel:Endpoints` refusé et adresses effectives vérifiées au démarrage. |
| Token d'accès | Durée configurée de 300 secondes ; Token Exchange borné par la validité restante du jeton source. |
| Refresh tokens | Émis aux clients interactifs qui déclarent et demandent `offline_access` ; réglages par défaut de Duende (réutilisation du handle, expiration absolue), en mémoire. |
| Assertions `private_key_jwt` | Seul `RS256` est annoncé (`token_endpoint_auth_signing_alg_values_supported`) et accepté. |
| Claim `scope` des JWT | Une chaîne séparée par des espaces (RFC 9068, RFC 8693), comme chez Keycloak ; jamais un tableau JSON. |
| Consentement | `RequireConsent = false`. |
| Claims utilisateur dans l'ID token | `AlwaysIncludeUserClaimsInIdToken = true`, sous réserve des claims effectivement disponibles et scopes demandés. |
| Gestion automatique des clés Duende | Désactivée ; clé de développement en mémoire, `persistKey: false`. |
| Protection des données | Fournisseur éphémère, application `AspireIdentityServer:{name}`. |
| Stockages | En mémoire. |
| Health check | `/health`. |
| Tableau de bord | Icône de fournisseur et lien « OpenID discovery ». Les secrets, clés et mots de passe générés s'appellent `{fournisseur}-{client ou utilisateur}-{secret, key ou password}` et apparaissent sous leur fournisseur. Un nom invalide ou déjà pris retombe sur un nom indexé. Ils sont régénérés à chaque lancement : aucune persistance n'est proposée. |
| Télémétrie | `AddIdentityServer()` appelle `WithOtlpExporter()`. Quand l'AppHost fournit un point OTLP, comme avec le tableau de bord Aspire, le fournisseur exporte ses traces (ASP.NET Core, HttpClient et sources Duende), ses métriques et ses journaux. Sans point OTLP, rien n'est configuré. Les valeurs de chaîne de requête restent masquées par l'instrumentation. |
| Interface | Razor Pages `/Account/Login`, `/Account/Logout`, `/Error`. |
| Cookies | Session `aspire-idp-{name}` et `aspire-idp-{name}.session` : `Secure`, `SameSite=Lax`. Antiforgery `aspire-idp-{name}.csrf` : `Secure`. Les cookies ne sont pas isolés par port : ils partent vers les autres applications `https://localhost` du poste. `Lax` suppose des clients sur `localhost` : un client servi sous un autre nom, comme `*.dev.localhost`, n'est pas « same-site », et la surveillance de session par iframe ou `prompt=none` ne recevraient pas la session. |
| En-têtes | Toutes les réponses : `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`. Pages `/Account/*` et `/Error` : CSP `default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'` et `Referrer-Policy: no-referrer`. |
| Racine `/` | Redirection vers `/.well-known/openid-configuration`. |

Les mécanismes généraux de Duende ne deviennent pas des fonctionnalités configurables de ce package du seul fait de sa dépendance. Aucun paramètre public n'est exposé ici pour la durée des refresh tokens, la fédération, CORS, la persistance ou la rotation durable de clés.

## Sources de référence

- [Extensions du fournisseur](../src/Aspire.IdentityServer.Hosting/IdentityServerExtensions.cs).
- [Ressource et options](../src/Aspire.IdentityServer.Hosting/IdentityServerResource.cs).
- [IdentityClient](../src/Aspire.IdentityServer.Hosting/IdentityClient.cs).
- [Projections](../src/Aspire.IdentityServer.Hosting/IdentityProjectionExtensions.cs).
- [Runtime](../src/Aspire.IdentityServer.Server/Program.cs).
- [Options client](../src/Aspire.IdentityServer.Client/IdentityClientOptions.cs) et [helper](../src/Aspire.IdentityServer.Client/PrivateKeyJwt.cs).
