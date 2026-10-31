# Clients, PKCE, secrets et clés privées

[Documentation](README.md) · [Fournisseurs](fournisseurs.md) · [Projections](projections.md)

Les extraits de cette page supposent `builder` et un fournisseur `idp` déjà déclarés. Chaque client possède un grant principal et une seule méthode d'authentification. La version `0.1.0-alpha.2` permet aussi d'activer explicitement le [Token Exchange](token-exchange.md) sur un client machine avec `WithTokenExchangeFrom(browser, api, userClaims: ["name"])`.

## Choisir le parcours

| Client | Méthode de création | Authentification | PKCE |
| --- | --- | --- | --- |
| Web/BFF confidentiel | `AddAuthorizationCodeClient` | Secret ou `private_key_jwt`. | Activé par défaut. |
| Client navigateur public | `AddAuthorizationCodeClient` | Aucune : ne configure pas de credentials. | Obligatoire. |
| Worker/API sortante | `AddClientCredentialsClient` | Secret ou `private_key_jwt`, obligatoire. | Sans objet. |

`WithScopes()` enregistre une liste autorisée. Le consommateur doit encore demander ces scopes lors du parcours OAuth. Les scopes API doivent exister sur une ressource du même fournisseur.

## Client machine

```csharp
idp.AddApiResource("orders-api").WithScopes("orders.read");
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");
```

Ce client demande des tokens avec `grant_type=client_credentials`, sans utilisateur ni redirection. Il doit avoir des credentials et uniquement des scopes API. Il ne reçoit pas de refresh token.

## Client web confidentiel

Extrait pour une ressource Aspire `web` existante ayant un endpoint `https` :

```csharp
var browser = idp.AddAuthorizationCodeClient("web")
    .WithGeneratedPrivateKeyJwt()
    .RequirePkce()
    .WithScopes("openid", "profile", "orders.read")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-oidc");

web.WithIdentityClient(browser, "Authentication:Corporate");
```

Le scope `orders.read` suppose une ressource API déjà déclarée. Un client interactif doit au minimum autoriser `openid` et posséder une redirection de connexion.

PKCE et authentification client sont indépendants : un client peut employer PKCE et `private_key_jwt` simultanément. `.RequirePkce()` rend explicite le défaut `true`. `.RequirePkce(false)` est accepté pour un client confidentiel ; un client public sans PKCE est refusé au démarrage. Le consommateur configure aussi son propre `UsePkce`.

## Client public

```csharp
var publicClient = idp.AddAuthorizationCodeClient("public-web")
    .RequirePkce()
    .WithScopes("openid", "profile")
    .WithRedirectUri(web.GetEndpoint("https"), "/signin-public")
    .WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-public");
```

Aucune méthode `With...Secret` ou `With...PrivateKeyJwt` n'est appelée. La méthode d'authentification reste `none` et aucune clé privée ni aucun secret ne sont projetés.

Le sample teste ce type de client avec le handler ASP.NET Core. Il ne fournit pas d'exemple de SPA React ni de configuration CORS pour un échange direct de tokens depuis JavaScript. Un BFF conserve ses credentials côté serveur ; ne projette pas une clé privée dans un bundle React.

## Secret généré

```csharp
var worker = idp.AddClientCredentialsClient("worker-secret")
    .WithGeneratedClientSecret()
    .WithScopes("orders.read");
```

Le secret contient 32 octets aléatoires encodés en Base64, générés une fois à l'enregistrement. Il est représenté par un paramètre secret Aspire. Le fournisseur ne reçoit que son empreinte, `Base64(SHA-256(secret))`, calculée par l'AppHost au lancement : c'est la seule valeur que Duende conserve, et le secret n'entre ni dans l'environnement ni dans la mémoire du fournisseur. Les consommateurs projetés reçoivent, eux, la référence au paramètre secret.

La projection indique `AuthenticationMethod = "client_secret_post"`. Le consommateur transmet `client_secret` dans le formulaire de demande de token. L'API fluide n'offre pas de sélection `client_secret_basic`.

## Secret fourni

```csharp
var secret = builder.AddParameter("orders-client-secret", secret: true);
var worker = idp.AddClientCredentialsClient("worker-existing-secret")
    .WithClientSecret(secret)
    .WithScopes("orders.read");
```

Alimente le paramètre `Parameters:orders-client-secret` via la configuration de l'AppHost, par exemple ses user secrets. Il doit être marqué `secret: true`. Il n'existe pas de surcharge `WithClientSecret(string)` dans cette version.

## Clé RSA générée

```csharp
var worker = idp.AddClientCredentialsClient("worker-jwt")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");
```

L'intégration crée une clé RSA 2048 bits. La clé privée est normalisée en PEM PKCS#8 et conservée dans un paramètre secret. Le `kid` est le SHA-256 hexadécimal en minuscules de la clé publique SubjectPublicKeyInfo. Le fournisseur reçoit un JWK public avec `kty=RSA`, `alg=RS256`, `use=sig`, `kid`, `n` et `e`.

La clé privée est injectée uniquement dans les consommateurs explicitement sélectionnés. Le provider enregistre la partie publique pour vérifier leurs assertions.

## Clé PEM fournie à l'AppHost

Pour utiliser `Base64(UTF8(JSON JWK privée))` directement, consulte [JWK privée Base64 : BFF et CC Xp/Capa](jwk-base64.md). La version `0.1.0-alpha.2` ajoute `WithGeneratedBase64PrivateKeyJwt()`, `WithBase64PrivateKeyJwt(string)` et `WithBase64PrivateKeyJwt(parametreSecret, publicJwk)` ; ces API ne figurent pas dans les packages déjà publiés `0.1.0-alpha.1`.

Exemple où `DevelopmentKeys:OrdersPrivateKeyPem` provient d'une configuration locale non suivie par Git :

```csharp
var privateKeyPem = builder.Configuration["DevelopmentKeys:OrdersPrivateKeyPem"]
    ?? throw new InvalidOperationException("Missing development RSA key.");

var worker = idp.AddClientCredentialsClient("worker-existing-key")
    .WithPrivateKeyJwt(privateKeyPem)
    .WithScopes("orders.read");
```

La clé doit être une clé privée RSA importable par `RSA.ImportFromPem`, d'au moins 2048 bits. Cette surcharge calcule le JWK public et le `kid`, puis normalise la clé privée en PEM PKCS#8. Les clés chiffrées nécessitant un mot de passe ne sont pas gérées par cette surcharge.

## Paramètre secret et JWK public fournis séparément

```csharp
var privateKey = builder.AddParameter("orders-private-key", secret: true);
var publicJwk = builder.Configuration["DevelopmentKeys:OrdersPublicJwk"]
    ?? throw new InvalidOperationException("Missing public RSA JWK.");

var worker = idp.AddClientCredentialsClient("worker-key-parameter")
    .WithPrivateKeyJwt(privateKey, publicJwk)
    .WithScopes("orders.read");
```

Cette surcharge garde la référence au paramètre secret sans en résoudre la valeur à l'enregistrement. Le JWK doit contenir `kty=RSA`, `kid`, `n` et `e`. Les paramètres RSA privés `d`, `p`, `q`, `dp`, `dq`, `qi`, `oth` sont refusés ; la clé publique doit être d'au moins 2048 bits.

La correspondance mathématique entre le paramètre privé et le JWK public n'est pas contrôlée pendant cet enregistrement différé. Fournis impérativement la paire correspondante ; sinon l'authentification échouera lors d'une demande de token. Le helper de signature vérifiera également que la clé privée est RSA et suffisamment longue.

Une méthode d'authentification déjà configurée ne peut pas être remplacée ou combinée avec une autre sur le même client : crée un autre enregistrement pour tester une autre méthode.

## Redirections

Les deux méthodes prennent un `EndpointReference` Aspire et un chemin :

```csharp
browser.WithRedirectUri(web.GetEndpoint("https"), "/signin-oidc");
browser.WithPostLogoutRedirectUri(web.GetEndpoint("https"), "/signout-callback-oidc");
```

Les URI sont composées par référence et résolues après allocation des ports. Tu peux ajouter plusieurs redirections à un client interactif. Le chemin doit commencer par `/`, sans commencer par `//`, et ne doit contenir ni `#` ni antislash.

Ces méthodes ne s'appliquent pas aux clients machine. Elles ne configurent pas automatiquement les chemins du handler OIDC du consommateur : ceux-ci doivent correspondre à l'enregistrement. Les chemins ne figurent pas dans la projection par défaut.

## Assertions `private_key_jwt`

Le package facultatif `TheDotSide.Aspire.IdentityServer.Client` expose `PrivateKeyJwt.CreateAssertion(client, issuer)`. Il suit le profil [draft-ietf-oauth-rfc7523bis](https://datatracker.ietf.org/doc/draft-ietf-oauth-rfc7523bis/), qui empêche l'injection d'audience : l'assertion ne vaut que pour l'issuer qui la reçoit.

| Élément | Valeur |
| --- | --- |
| Signature | RSA SHA-256 / `RS256`. |
| En-tête `typ` | `client-authentication+jwt` (`PrivateKeyJwt.ClientAuthenticationType`). |
| En-tête `kid` | PEM : `IdentityClientOptions.KeyId`. JWK Base64 : `kid` de la JWK ; `KeyId`, si fourni, doit correspondre. |
| `iss` et `sub` | `ClientId`. |
| `aud` | L'issuer du fournisseur, seul et en chaîne : la valeur `issuer` de son document de découverte. |
| `iat` | Heure UTC courante. |
| `nbf` | Heure courante moins 5 secondes. |
| `exp` | Heure courante plus 1 minute. |
| `jti` | Nouveau GUID à chaque appel. |

Le fournisseur local exige ce profil par défaut (`StrictClientAssertionAudienceValidation` de Duende). Pour un fournisseur réel qui refuse encore l'issuer comme audience, `PrivateKeyJwt.CreateTokenEndpointAssertion(client, tokenEndpoint)` produit le profil RFC 7523 d'origine : audience = token endpoint, `typ: JWT`. Pour tester un tel client contre le fournisseur local, active explicitement `AcceptTokenEndpointAudience` :

```csharp
var idp = builder.AddIdentityServer("idp", options => options.AcceptTokenEndpointAudience = true);
```

Une assertion typée `client-authentication+jwt` reste toujours validée strictement, même avec cette option.

Crée une assertion neuve pour chaque requête, y compris une nouvelle tentative. Ne conserve pas une assertion en configuration ; la protection contre le rejeu est testée. Le helper ne découvre pas les endpoints et n'envoie pas de requête HTTP. Voir [l'intégration](integration.md).
