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
| `idp.WithIdentityScope(string name, params string[] userClaims)` | Builder du fournisseur. | Depuis `0.1.0-alpha.4` : scope d'identité personnalisé, nom unique, hors `openid`/`profile`/`email`/`offline_access` et scopes d'API ; au moins un claim utilisateur. Depuis alpha.5, un scope demandé par un client Authorization Code sélectionne aussi ses claims dans l'access token de connexion, en plus de l'ID token et UserInfo. Voir [communities/member_of](fournisseurs.md#claims-des-scopes-didentité-dans-laccess-token). |
| `idp.WithSimulatedMfa(string code, params string[] acrValues)` | Builder du fournisseur. | Depuis `11.3.0` : MFA simulée. Code de 4 à 10 chiffres, valeur fixe de développement et non secrète. Au moins un niveau `acr`, sans espace, sans préfixe `idp:` ni `tenant:`, sans doublon. Un seul appel par fournisseur. Voir [MFA simulée](fournisseurs.md#mfa-simulée). |
| `idp.AddApiResource(string audience)` | `IdentityApiResource` | Audience non vide, unique dans le fournisseur. |
| `apiResource.WithScopes(params string[] scopes)` | Même ressource API. | Ajout cumulatif ; au moins un scope au démarrage. Scopes d'identité et `offline_access` réservés. |

### Options et propriétés

| Type / propriété | Type de valeur | Rôle |
| --- | --- | --- |
| `IdentityServerOptions.RuntimeDirectory` | `string` modifiable. | Dossier contenant le serveur ; défaut `AppContext.BaseDirectory/identityserver`. Chemin relatif résolu depuis l'AppHost. |
| `IdentityServerOptions.StrictClientAssertionAudienceValidation` | `bool`, défaut `true`. | Depuis `11.4.0` : n'accepte que le profil draft-ietf-oauth-rfc7523bis : l'issuer comme seule audience et le type `client-authentication+jwt`. À `false`, accepte aussi les assertions RFC 7523 d'origine, dont l'audience est le token endpoint. Remplace `AcceptTokenEndpointAudience`, de sens inverse, qui marche encore mais est obsolète. |
| `IdentityServerResource.RegisteredClients` | `IReadOnlyList<IdentityClient>` | Consultation des enregistrements ; utilisée par les tests. |
| `IdentityServerResource.RegisteredUsers` | `IReadOnlyList<IdentityTestUser>` | Depuis `11.1.0` : utilisateurs de test, dans l'ordre de déclaration ; utilisés par le package `Testing`. |
| `IdentityServerResource.SimulatedMfa` | `IdentitySimulatedMfa?` | Depuis `11.3.0` : code (`Code`) et niveaux `acr` (`AcrValues`) déclarés par `WithSimulatedMfa`, sinon `null` ; utilisé par le package `Testing`. |
| `IdentityApiResource.Server` | Builder du fournisseur. | Fournisseur associé. |
| `IdentityApiResource.Audience` | `string` | Audience de la ressource. |
| `IdentityApiResource.Authority` | `EndpointReference` | Endpoint `https` différé. |
| `IdentityApiResource.Scopes` | `IReadOnlyCollection<string>` | Ensemble des scopes de la ressource. |
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
| `WithTokenExchangeFrom(IdentityClient subjectClient, IdentityApiResource api, params string[] userClaims)` | Depuis `0.1.0-alpha.2` : active l'échange pour un client machine, une source Authorization Code et une API du même IdP. `sub` conservé ; autres claims explicitement sélectionnés. Depuis `11.1.0`, la source peut appartenir à un autre IdP : confiance directionnelle, sujet local `{IdP source}|{sub}`. Voir [Token Exchange](token-exchange.md#entre-deux-idp). |
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
| `WithRedirectUri(Uri redirectUri)`, `WithPostLogoutRedirectUri(Uri postLogoutRedirectUri)` | Depuis `11.1.0` : callback littéral pour un outil hors de l'AppHost (Postman, Bruno, CLI, application native) : adresse HTTPS, adresse HTTP loopback, ou schéma privé à nom de domaine inversé (`com.example.app:/callback`, RFC 8252). Fragment, informations d'utilisateur et autres schémas refusés. Comparaison exacte, port compris. |
| `WithAllowedCorsOrigin(EndpointReference endpoint)` | Depuis `11.1.0` : autorise l'origine de cet endpoint à appeler en CORS la découverte, le JWKS, le token endpoint et UserInfo, comme une SPA. Client interactif uniquement. |
| `WithAccessTokenLifetime(TimeSpan lifetime)` | Depuis `11.1.0` : durée des jetons d'accès du client, d'une seconde à un jour ; 300 secondes par défaut. |
| `WithRefreshTokenLifetime(TimeSpan lifetime)` | Depuis `11.1.0` : durée absolue des refresh tokens d'un client interactif, d'une seconde à 30 jours (défaut de Duende). Exige `offline_access`, vérifié au démarrage. |
| `WithBackChannelLogoutUri(EndpointReference endpoint, string path)` | Depuis `11.1.0` : le fournisseur poste un logout token signé (avec `sid`) à cette adresse quand la session se termine (OpenID Connect Back-Channel Logout 1.0). |
| `WithFrontChannelLogoutUri(EndpointReference endpoint, string path)` | Depuis `11.1.0` : adresse chargée dans une iframe de la page de déconnexion du fournisseur (OpenID Connect Front-Channel Logout 1.0). |

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
| `GrantType` | `string` | Depuis `11.1.0` : `authorization_code` ou `client_credentials`. |
| `RegisteredRedirectUris` | `IReadOnlyList<ReferenceExpression>` | Depuis `11.1.0` : callbacks de connexion différés, dans l'ordre de déclaration. |
| `Scopes` | `IReadOnlyCollection<string>` | Scopes autorisés. |
| `TokenExchangeAudience` | `string?` | Depuis `0.1.0-alpha.2` : audience unique des règles d'échange, sinon null. |

Ces propriétés sont consultables ; les enregistrements se modifient par les méthodes fluides. Les URI de déconnexion ne sont pas exposées.

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
| `PrivateKeyJwt.CreateTokenEndpointAssertion(IdentityClientOptions client, string tokenEndpoint)` | Profil RFC 7523 d'origine, pour un fournisseur qui refuse l'issuer : `typ: JWT`, `aud` = token endpoint. Refusé par le fournisseur local tant que `StrictClientAssertionAudienceValidation` vaut `true`. |

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

Pas de cache de jeton utilisateur, de contexte HTTP ou de token échangé dans le handler partagé. Un jeton absent, une adresse non HTTPS, un header Authorization préexistant ou une réponse d'échange invalide provoquent un échec. Une erreur du token endpoint lève `TokenEndpointException`. Voir le [sample et le guide d'intégration](token-exchange.md#appeler-lapi-avec-un-httpclient-nommé).

### HttpClient nommé et client credentials

`IHttpClientBuilder.AddClientCredentialsHandler(IConfigurationSection clientConfiguration, Action<ClientCredentialsHandlerOptions>? configure = null)` ajoute un jeton `client_credentials` à chaque requête du client nommé. La section est bindée sur des options `IdentityClientOptions` nommées comme ce client. Le handler partage la découverte et le backchannel du handler d'échange : chaque tentative de demande de jeton porte une assertion neuve, y compris derrière les handlers de résilience.

| Réglage | Comportement |
| --- | --- |
| `ClientCredentialsHandlerOptions.Scope` | Scopes explicites ; sinon scopes projetés. Vides tous les deux : le paramètre est omis et le fournisseur accorde les scopes autorisés au client. |
| `ClientCredentialsHandlerOptions.AllowedOrigins` | Origines (schéma, hôte, port) autorisées à recevoir le jeton. Vide par défaut : toute adresse HTTPS du client nommé. |
| `ClientCredentialsHandlerOptions.BackchannelTimeout` | Budget de la découverte et de la demande de jeton, tentatives comprises ; 30 s par défaut. Dépassé : `TimeoutException`. |
| `ClientCredentialsHandlerOptions.RefreshBeforeExpiration` | Délai avant l'expiration (`expires_in`) auquel un jeton en cache est renouvelé ; 30 s par défaut. |

Le jeton est mis en cache par client nommé et par scope ; des requêtes simultanées partagent une seule demande. Une réponse 401 de la ressource l'écarte, par exemple après le redémarrage du fournisseur avec une nouvelle clé de signature : la requête suivante en obtient un autre, sans rejouer celle qui a échoué. Une adresse non HTTPS, une origine non autorisée ou un header Authorization préexistant provoquent un échec.

`ClientCredentials.CreateRequest(IdentityClientOptions client, string tokenEndpoint, string? scope = null, string? issuer = null)` crée la requête `client_credentials` seule, avec des credentials neufs, pour une bibliothèque qui envoie elle-même ses requêtes. La constante `ClientCredentials.GrantType` expose l'identifiant standard.

`TokenEndpointException`, dérivée d'`HttpRequestException`, porte le statut HTTP, `Error` (par exemple `invalid_scope`, `invalid_target`) et `ErrorDescription` renvoyés par le token endpoint, pour les deux handlers.

## Package Authentication

Extensions ASP.NET Core du package facultatif `TheDotSide.Aspire.IdentityServer.Authentication`, dans l'espace de noms `Microsoft.Extensions.DependencyInjection` (et `Microsoft.AspNetCore.Authorization` pour `RequireScope`). Voir [l'intégration](integration.md).

| Membre | Comportement |
| --- | --- |
| `AuthenticationBuilder.AddIdentityServerJwtBearer(string authenticationScheme, IConfigurationSection apiConfiguration, Action<JwtBearerOptions>? configure = null)` | Lit `Authority` et `Audience` projetés par `WithIdentityApiResource` ; `MapInboundClaims = false`, claims `name` et `role`. Valeur absente : `InvalidOperationException` qui nomme la clé. `configure` s'applique ensuite. |
| `AuthenticationBuilder.AddIdentityServerOpenIdConnect(string authenticationScheme, IConfigurationSection clientConfiguration, Action<OpenIdConnectOptions>? configure = null)` | Lit la section projetée par `WithIdentityClient` : authority, client ID, scopes, secret pour `client_secret_post` ; `code`, PKCE, `SaveTokens`, noms de claims JWT. Pour `private_key_jwt`, signe une assertion neuve (audience = issuer découvert) dans `OnAuthorizationCodeReceived` et `OnPushAuthorization`, après les événements de `configure`. Depuis `11.4.0`, une connexion annulée (`access_denied`) revient à la racine de l'application : `AccessDeniedPath = "/"`, que `configure` peut remplacer. |
| `AuthorizationPolicyBuilder.RequireScope(params string[] scopes)` | Exige au moins un des scopes dans le claim `scope` séparé par des espaces. |

## Package Testing

Extensions de `DistributedApplication`, dans l'espace de noms `Aspire.Hosting.Testing`, pour une application démarrée. Voir [les tests d'intégration](tests.md).

| Membre | Comportement |
| --- | --- |
| `GetUserTokensAsync(string provider, string clientId, string username, string? scope = null, string? acrValues = null, CancellationToken cancellationToken = default)` | Depuis `11.3.0`, `acrValues` envoie `acr_values` et saisit le code de la MFA simulée du fournisseur. Connexion authorization code + PKCE par le formulaire du fournisseur, sans navigateur ; échange du code avec l'authentification du client. Client d'authorization code avec redirect URI exigé. |
| `GetClientCredentialsTokensAsync(string provider, string clientId, string? scope = null, CancellationToken cancellationToken = default)` | Jeton `client_credentials` d'un client machine. |
| `GetIdentityClientOptionsAsync(string provider, string clientId, CancellationToken cancellationToken = default)` | `IdentityClientOptions` avec credentials résolus, comme la projection standard. |
| `IdentityTokens` | `AccessToken`, `TokenType`, `IdToken`, `RefreshToken`, `Scope`, `ExpiresIn`. |

Un nom inconnu ou un client du mauvais type lève `ArgumentException` ; une erreur du token endpoint, `HttpRequestException`.

## Réglages fixes du runtime

Ces valeurs sont celles du code actuel, sans option fluide associée :

| Réglage | Valeur / comportement |
| --- | --- |
| Contrat de configuration | `IdentityServer__ContractVersion`, version `4` depuis `11.1.0`. Le runtime refuse de démarrer si l'AppHost écrit une autre version : Hosting et Runtime doivent avoir la même version de paquet. |
| Environnement accepté | Celui de l'AppHost, `Development` ou `Testing` ; `AddIdentityServer()` refuse les autres et le runtime les refuse aussi. Un avertissement est journalisé à chaque démarrage. |
| Issuer | URI absolue HTTPS loopback exigée. |
| Écoute | HTTPS sur `localhost` uniquement, au port `ASPNETCORE_HTTPS_PORTS` fixé par Aspire ; `Kestrel:Endpoints` refusé et adresses effectives vérifiées au démarrage. |
| Token d'accès | 300 secondes par défaut, `WithAccessTokenLifetime` par client ; Token Exchange borné par la validité restante du jeton source. |
| Refresh tokens | Émis aux clients interactifs qui déclarent et demandent `offline_access` ; réglages par défaut de Duende (réutilisation du handle, expiration absolue de 30 jours, `WithRefreshTokenLifetime` par client), en mémoire. |
| Assertions `private_key_jwt` | Seul `RS256` est annoncé (`token_endpoint_auth_signing_alg_values_supported`) et accepté. |
| Claim `scope` des JWT | Une chaîne séparée par des espaces (RFC 9068, RFC 8693), comme chez Keycloak ; jamais un tableau JSON. |
| Consentement | `RequireConsent = false`. |
| Claims utilisateur dans l'ID token | `AlwaysIncludeUserClaimsInIdToken = true`, sous réserve des claims effectivement disponibles et scopes demandés. |
| Gestion automatique des clés Duende | Désactivée. Depuis `11.1.0`, l'AppHost génère une clé RSA 2048 par fournisseur, transmise comme paramètre secret `{fournisseur}-signing-key` : un fournisseur redémarré depuis le tableau de bord la retrouve, ses jetons précédents restent valides et les clés en cache des API continuent de correspondre. Nouvelle clé à chaque lancement de l'AppHost. |
| Protection des données | Fournisseur éphémère, application `AspireIdentityServer:{name}`. |
| Stockages | En mémoire. |
| Health check | `/health`. |
| Tableau de bord | Icône de fournisseur et lien « OpenID discovery ». Depuis `11.1.0`, deux commandes, actives quand le fournisseur tourne : **Show registrations** affiche en Markdown ses clients, ressources API, scopes d'identité et utilisateurs de test, sans secret ; **Request a client token**, proposée s'il a des clients machine, demande un jeton `client_credentials` pour le client choisi, avec son authentification réelle, et l'affiche avec ses claims. L'API des commandes de ressource d'Aspire les rend aussi découvrables par d'autres clients que le tableau de bord. Les secrets, clés et mots de passe générés s'appellent `{fournisseur}-{client ou utilisateur}-{secret, key ou password}` et apparaissent sous leur fournisseur. Un nom invalide ou déjà pris retombe sur un nom indexé. Ils sont régénérés à chaque lancement : aucune persistance n'est proposée. |
| Télémétrie | `AddIdentityServer()` appelle `WithOtlpExporter()`. Quand l'AppHost fournit un point OTLP, comme avec le tableau de bord Aspire, le fournisseur exporte ses traces (ASP.NET Core, HttpClient et sources Duende), ses métriques et ses journaux. Sans point OTLP, rien n'est configuré. Les valeurs de chaîne de requête restent masquées par l'instrumentation. |
| Interface | Razor Pages `/Account/Login`, `/Account/Logout`, `/Error`. `login_hint` pré-remplit l'utilisateur ; le mot de passe reste exigé. **Cancel** renvoie l'erreur standard `access_denied` au client. Une déconnexion qui présente un `id_token_hint` valide se fait sans confirmation et revient à `post_logout_redirect_uri`, après le chargement de l'éventuelle iframe de notification ; toute autre demande de déconnexion est confirmée par un POST. Les pages partagent une mise en page en thème clair ou sombre, selon le système. La connexion nomme l'application cliente ; la page d'erreur affiche l'erreur OAuth, sa description, le client et l'identifiant de requête. Depuis `11.4.0`, la page `/Account/Mfa` de la MFA simulée dessine une case par chiffre du code autour d'un champ unique, sans script : coller le code et le remplissage automatique (`autocomplete="one-time-code"`) fonctionnent. La feuille de style et l'icône, embarquées dans le runtime, sont servies par le fournisseur (`/Account/site.css`, `/Account/icon.svg`) : la CSP des pages refuse tout style ou script en ligne. |
| Cookies | Session `aspire-idp-{name}` et `aspire-idp-{name}.session` : `Secure`, `SameSite=Lax`. Antiforgery `aspire-idp-{name}.csrf` : `Secure`. Les cookies ne sont pas isolés par port : ils partent vers les autres applications `https://localhost` du poste. `Lax` suppose des clients sur `localhost` : un client servi sous un autre nom, comme `*.dev.localhost`, n'est pas « same-site », et la surveillance de session par iframe ou `prompt=none` ne recevraient pas la session. |
| En-têtes | Toutes les réponses : `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`. Pages `/Account/*` et `/Error` : CSP `default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'` et `Referrer-Policy: no-referrer`. |
| Racine `/` | Redirection vers `/.well-known/openid-configuration`. |

Les mécanismes généraux de Duende ne deviennent pas des fonctionnalités configurables de ce package du seul fait de sa dépendance. Aucun paramètre public n'est exposé ici pour la fédération, la persistance ou la rotation durable de clés.

## Sources de référence

- [Extensions du fournisseur](../src/Aspire.IdentityServer.Hosting/IdentityServerExtensions.cs).
- [Ressource et options](../src/Aspire.IdentityServer.Hosting/IdentityServerResource.cs).
- [IdentityClient](../src/Aspire.IdentityServer.Hosting/IdentityClient.cs).
- [Projections](../src/Aspire.IdentityServer.Hosting/IdentityProjectionExtensions.cs).
- [Runtime](../src/Aspire.IdentityServer.Server/Program.cs).
- [Options client](../src/Aspire.IdentityServer.Client/IdentityClientOptions.cs) et [helper](../src/Aspire.IdentityServer.Client/PrivateKeyJwt.cs).
