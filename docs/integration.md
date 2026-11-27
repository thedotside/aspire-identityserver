# Intégrer une API, une application web ou un BFF

[Documentation](README.md) · [Projections](projections.md) · [Kubernetes](kubernetes.md)

La projection fournit des valeurs. Le consommateur configure sa bibliothèque OAuth/OIDC, son middleware JWT et ses règles d'autorisation. Le package `Client` est facultatif et n'ajoute aucun de ces services automatiquement.

## API : valider les tokens entrants

Dans l'AppHost, pour une ressource `orders` et un projet `api` existants :

```csharp
api.WithIdentityApiResource(orders, "Authentication:Inbound");
```

Dans l'API, le package facultatif `TheDotSide.Aspire.IdentityServer.Authentication` lit cette section :

```csharp
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication()
    .AddIdentityServerJwtBearer("Bearer", builder.Configuration.GetSection("Authentication:Inbound"));
builder.Services.AddAuthorization(options =>
    options.AddPolicy("orders.read", policy => policy
        .AddAuthenticationSchemes("Bearer")
        .RequireAuthenticatedUser()
        .RequireScope("orders.read")));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/orders", () => new[] { "order-001" })
    .RequireAuthorization("orders.read");
app.Run();
```

`AddIdentityServerJwtBearer` fixe `Authority` et `Audience` depuis la section, garde les noms de claims JWT (`MapInboundClaims = false`) et prend `name` comme claim de nom. Le handler vérifie notamment signature, issuer, audience et expiration avec les métadonnées du fournisseur. Le fournisseur émet `scope` sous forme d'une chaîne séparée par des espaces (RFC 9068) : `RequireScope` exige au moins un des scopes cités, ce que `RequireClaim` ne sait pas faire. Une opération d'écriture doit demander une policy distincte pour `orders.write`.

Sans ce package, configure `AddJwtBearer` avec `Authority = section["Authority"]`, `Audience = section["Audience"]` et `MapInboundClaims = false`, puis découpe le claim `scope` dans une `RequireAssertion`.

Pour deux IDP, enregistre deux schémas avec leurs sections propres, puis sélectionne le schéma dans les policies. [Sample.Api](../samples/Sample.Api/Program.cs) fournit cette configuration ; n'agrège pas les deux autorités dans une validation permissive.

## Application web : authorization code et PKCE

L'AppHost déclare le client, ses redirections et la projection selon [le guide clients](clients.md). L'application lit la section projetée avec le package `Authentication` :

```csharp
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "web-cookie";
        options.DefaultChallengeScheme = "web-oidc";
    })
    .AddCookie("web-cookie", options => options.Cookie.Name = "orders-web")
    .AddIdentityServerOpenIdConnect(
        "web-oidc",
        builder.Configuration.GetSection("Authentication:Corporate"),
        options =>
        {
            options.SignInScheme = "web-cookie";
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-callback-oidc";
        });
```

`AddIdentityServerOpenIdConnect` fixe l'authority, le client ID, les scopes projetés, `ResponseType = code`, PKCE, `SaveTokens`, les noms de claims JWT et `name` comme claim de nom. Un client `client_secret_post` envoie son secret. Un client `private_key_jwt` signe une assertion neuve, dont l'audience est l'issuer découvert, pour l'échange du code **et pour la requête PAR** : PAR peut rester activé, comme le fait le handler ASP.NET Core par défaut quand le fournisseur l'annonce. Le callback `configure` s'applique après ces valeurs, pour les chemins, cookies ou `MaxAge` ; les événements qu'il définit sont conservés. [Sample.Web](../samples/Sample.Web/Program.cs) utilise ce helper pour ses quatre schémas, dont `par`, qui exige PAR avec le client à clé privée.

Pour ouvrir l'inscription d'un fournisseur déclaré avec [`WithSignUp`](fournisseurs.md#inscription-promptcreate), par exemple depuis un bouton « Créer un compte », l'application envoie `prompt=create` dans le challenge :

```csharp
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

app.MapGet("/signup", () => Results.Challenge(
    new OpenIdConnectChallengeProperties { RedirectUri = "/", Prompt = "create" },
    ["web-oidc"]));
```

Le fournisseur affiche sa page de création de compte, puis revient au callback avec l'utilisateur connecté, comme après une connexion ; PAR reste possible. La demande garde le scope `openid`, que le handler OpenID Connect envoie toujours : sans lui, le fournisseur refuse `prompt=create` avec `invalid_request`. Un fournisseur sans `WithSignUp` refuse `prompt=create` avec `invalid_request`, comme un fournisseur réel qui ne gère pas l'inscription.

Sans ce package, configure `AddOpenIdConnect` avec les mêmes valeurs. Pour `private_key_jwt`, renseigne `ClientAssertionType` et `ClientAssertion` (avec `PrivateKeyJwt.CreateAssertion(client, issuer)`) dans `OnAuthorizationCodeReceived`, et dans `OnPushAuthorization` suivi de `HandleClientAuthentication()`, ou désactive PAR.

## Plusieurs connexions et déconnexion

Pour chaque fournisseur web, configure des noms de schémas, cookies, callbacks de connexion, callbacks de déconnexion et chemins de déconnexion distante distincts. [Sample.Web](../samples/Sample.Web/Program.cs) montre les schémas `oidc-a`, `oidc-b`, `oidc-public` et leurs cookies.

Le sample expose des routes de connexion et des formulaires POST de déconnexion protégés par antiforgery. Il déconnecte le schéma OIDC puis le cookie applicatif correspondant. Comme un fournisseur de production, l'IDP déconnecte sans confirmation une application qui présente son `id_token_hint`, ce que fait le handler ASP.NET Core avec `SaveTokens = true`, puis revient à son `post_logout_redirect_uri`. Une demande sans ce jeton est confirmée par un POST sur la page de déconnexion de l'IDP. Les tests navigateur couvrent la coexistence des sessions A/B, la déconnexion indépendante, la confirmation, `login_hint` et l'annulation (`access_denied`).

Ne remplace pas ces formulaires par un simple GET modifiant la session. La version à un fournisseur ci-dessus illustre uniquement la connexion et l'accès protégé ; pour le parcours complet de déconnexion, reprends le sample.

## Client machine : demander un token

Projette d'abord le client vers le composant sortant, par exemple `Integrations:Accounting:OAuth`. Dans un consommateur référençant le package `Client`, `AddClientCredentialsHandler` ajoute le jeton à chaque requête d'un `HttpClient` nommé :

```csharp
using Aspire.IdentityServer.Client;

builder.Services.AddHttpClient("accounting", http =>
        http.BaseAddress = new Uri("https://accounting.example"))
    .AddClientCredentialsHandler(
        builder.Configuration.GetSection("Integrations:Accounting:OAuth"),
        options => options.AllowedOrigins.Add(new Uri("https://accounting.example")));
```

Le handler découvre le token endpoint et l'issuer, authentifie le client par sa clé privée ou son secret, puis met le jeton en cache jusqu'à 30 secondes avant son expiration. Des requêtes simultanées partagent une seule demande de jeton, et chaque tentative de cette demande porte une assertion neuve : les handlers de résilience des ServiceDefaults peuvent rester en place. Une réponse 401 de l'API écarte le jeton, par exemple après le redémarrage du fournisseur avec une nouvelle clé de signature ; la requête suivante en obtient un autre. `AllowedOrigins` limite les adresses qui reçoivent le jeton. Une erreur du token endpoint lève `TokenEndpointException`, avec son code OAuth (`invalid_client`, `invalid_scope`…) et sa description. [Sample.Api](../samples/Sample.Api/Program.cs) l'utilise pour appeler une API partenaire.

Pour une autre bibliothèque HTTP, `ClientCredentials.CreateRequest` construit seulement la requête, avec des credentials neufs, à partir du token endpoint et de l'issuer découverts :

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.IdentityServer.Client;

static async Task<string> RequestTokenAsync(
    HttpClient http, IdentityClientOptions client, CancellationToken cancellationToken)
{
    var metadata = await http.GetFromJsonAsync<JsonElement>(
        $"{client.Authority}/.well-known/openid-configuration", cancellationToken);
    using var request = ClientCredentials.CreateRequest(
        client,
        metadata.GetProperty("token_endpoint").GetString()!,
        issuer: metadata.GetProperty("issuer").GetString());
    using var response = await http.SendAsync(request, cancellationToken);
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    return result.GetProperty("access_token").GetString()
        ?? throw new InvalidOperationException("Missing access token.");
}
```

Cet extrait ne met ni les métadonnées ni le jeton en cache. Une assertion `private_key_jwt` sert une seule fois : sans le backchannel du handler, une nouvelle tentative doit recréer la requête plutôt que la rejouer.

## Duende BFF et gestion des tokens

Le [guide JWK Base64](jwk-base64.md) détaille les configurations BFF et CC Xp/Capa avec `PrivateKeyBase64`, les appels remplaçant une adaptation PEM et l'injection Kubernetes.

La projection peut alimenter n'importe quelle section lue par un BFF .NET ou ses composants de gestion des tokens. Elle fournit l'authority, le client ID, les scopes et les credentials ; le BFF configure ensuite sa bibliothèque OIDC et ses clients sortants.

Cette alpha n'expose ni `AddDuendeBff()` ni adaptateur automatique pour les options des différentes versions de Duende BFF. Une clé PEM injectée ne suffit pas à activer les assertions dans cette bibliothèque. Configure ses points d'extension appropriés, y compris pour PAR s'il est actif.

Les exemples exécutés dans ce dépôt utilisent le handler OIDC ASP.NET Core standard. Ils ne constituent pas une validation d'intégration avec un package Duende BFF.

## React

Pour une architecture React + BFF, projette la clé ou le secret vers le BFF .NET, qui réalise le parcours confidentiel et les appels sortants. React utilise les routes et la session applicative du BFF.

Pour une SPA sans BFF, enregistre un client public avec PKCE et autorise l'origine de l'application en CORS, par exemple avec l'hébergement JavaScript d'Aspire :

```csharp
var spa = builder.AddViteApp("spa", "../spa");
idp.AddAuthorizationCodeClient("spa")
    .WithScopes("openid", "profile", "orders.read")
    .WithRedirectUri(spa.GetEndpoint("http"), "/callback")
    .WithAllowedCorsOrigin(spa.GetEndpoint("http"));
```

Une SPA ne lit pas les variables d'environnement du serveur à l'exécution : sers-lui sa configuration (authority, client ID, scopes), comme le fait `/spa/config.json` dans [Sample.Web](../samples/Sample.Web/Program.cs). Sa page `/spa/`, sans dépendance, échange le code et appelle UserInfo depuis le navigateur ; une application réelle utilise une bibliothèque maintenue comme oidc-client-ts. Le CORS du fournisseur couvre la découverte, le JWKS, le token endpoint et UserInfo ; une API appelée par la SPA configure son propre CORS. Les consommateurs dans des conteneurs ne sont pas validés : l'issuer reste local. N'ajoute jamais une clé privée au code JavaScript.

## Déconnexion back-channel

`WithBackChannelLogoutUri(web.GetEndpoint("https"), "/backchannel-logout")` fait poster par le fournisseur un logout token signé, avec le `sid` de la session, quand l'utilisateur se déconnecte du fournisseur. L'application valide ce jeton (issuer, audience = son client ID, signature, événement `http://schemas.openid.net/event/backchannel-logout`) puis termine la session correspondante. [Sample.Web](../samples/Sample.Web/Program.cs) montre la validation et enregistre seulement le `sid` reçu ; ASP.NET Core ne fournit pas ce traitement. `WithFrontChannelLogoutUri` charge à la place une adresse de l'application dans une iframe de la page de déconnexion.
