# Intégrer une API, une application web ou un BFF

[Documentation](README.md) · [Projections](projections.md) · [Kubernetes](kubernetes.md)

La projection fournit des valeurs. Le consommateur configure sa bibliothèque OAuth/OIDC, son middleware JWT et ses règles d'autorisation. Le package `Client` est facultatif et n'ajoute aucun de ces services automatiquement.

## API : valider les tokens entrants

Dans l'AppHost, pour une ressource `orders` et un projet `api` existants :

```csharp
api.WithIdentityApiResource(orders, "Authentication:Inbound");
```

Dans l'API référençant `Microsoft.AspNetCore.Authentication.JwtBearer`, ce `Program.cs` illustre le comportement du sample avec un fournisseur :

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Inbound:Authority"];
        options.Audience = builder.Configuration["Authentication:Inbound:Audience"];
        options.MapInboundClaims = false;
    });

builder.Services.AddAuthorization(options =>
    options.AddPolicy("orders.read", policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.FindAll("scope")
            .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("orders.read")))));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/orders", () => new[] { "order-001" })
    .RequireAuthorization("orders.read");
app.Run();
```

Le handler vérifie notamment signature, issuer, audience et expiration avec les métadonnées du fournisseur. La policy applique le scope de lecture. Une opération d'écriture doit demander une policy distincte pour `orders.write`.

Pour deux IDP, enregistre deux schémas JWT avec leurs autorités et audiences propres, puis sélectionne le schéma dans les policies. [Sample.Api](../samples/Sample.Api/Program.cs) fournit cette configuration ; n'agrège pas les deux autorités dans une validation permissive.

## Application web : authorization code et PKCE

L'AppHost déclare le client, ses redirections et la projection selon [le guide clients](clients.md). Le consommateur référence `Microsoft.AspNetCore.Authentication.OpenIdConnect` et, pour cet exemple, `TheDotSide.Aspire.IdentityServer.Client`.

Ce `Program.cs` adapte le handler du sample pour la section `Authentication:Corporate` et les callbacks `/signin-oidc`, `/signout-callback-oidc` :

```csharp
using Aspire.IdentityServer.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
var client = builder.Configuration.GetSection("Authentication:Corporate")
    .Get<IdentityClientOptions>()
    ?? throw new InvalidOperationException("Missing OAuth client configuration.");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "web-cookie";
        options.DefaultChallengeScheme = "web-oidc";
    })
    .AddCookie("web-cookie", options => options.Cookie.Name = "orders-web")
    .AddOpenIdConnect("web-oidc", options =>
    {
        options.SignInScheme = "web-cookie";
        options.Authority = client.Authority;
        options.ClientId = client.ClientId;
        options.ClientSecret = client.ClientSecret;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.Scope.Clear();
        foreach (var scope in client.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(scope);

        if (client.AuthenticationMethod == "private_key_jwt")
        {
            options.Events.OnAuthorizationCodeReceived = async context =>
            {
                var metadata = await context.Options.ConfigurationManager!
                    .GetConfigurationAsync(context.HttpContext.RequestAborted);
                context.TokenEndpointRequest!.ClientAssertionType = PrivateKeyJwt.AssertionType;
                context.TokenEndpointRequest.ClientAssertion =
                    PrivateKeyJwt.CreateAssertion(client, metadata.Issuer);
            };
        }
    });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", (HttpContext context) => new { name = context.User.Identity?.Name })
    .RequireAuthorization();
app.Run();
```

Le handler déclenche la redirection et traite le callback. Avec un client secret, il utilise le secret projeté. Avec un client public, celui-ci est absent et PKCE reste activé. Avec `private_key_jwt`, l'événement fournit une assertion fraîche lors de l'échange du code.

PAR est désactivé dans cet exemple, comme dans le sample. Si ta bibliothèque utilise PAR, son appel doit aussi être authentifié correctement ; la projection d'une clé ne configure pas ce comportement.

## Plusieurs connexions et déconnexion

Pour chaque fournisseur web, configure des noms de schémas, cookies, callbacks de connexion, callbacks de déconnexion et chemins de déconnexion distante distincts. [Sample.Web](../samples/Sample.Web/Program.cs) montre les schémas `oidc-a`, `oidc-b`, `oidc-public` et leurs cookies.

Le sample expose des routes de connexion et des formulaires POST de déconnexion protégés par antiforgery. Il déconnecte le schéma OIDC puis le cookie applicatif correspondant. La page de déconnexion de l'IDP traite elle aussi un POST. Les tests navigateur couvrent la coexistence des sessions A/B et la déconnexion indépendante.

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

Pour une SPA sans BFF, enregistre un client public avec PKCE. La version actuelle n'offre pas de sample React, de configuration CORS ou de réécriture d'issuer adaptée à ce parcours dans des conteneurs. N'ajoute pas une clé privée au code JavaScript pour contourner cette limite.
