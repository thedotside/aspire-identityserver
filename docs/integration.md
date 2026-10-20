# Intégrer une API, une application web ou un BFF

[Documentation](README.md) · [Projections](projections.md) · [Kubernetes](kubernetes.md)

La projection fournit des valeurs. Le consommateur configure sa bibliothèque OAuth/OIDC, son middleware JWT et ses règles d'autorisation. Le package `Client` est facultatif et n'ajoute aucun de ces services automatiquement.

## API : valider les tokens entrants

Dans l'AppHost, pour une ressource `orders` et un projet `api` existants :

```csharp
api.WithIdentityResource(orders, "Authentication:Inbound");
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

L'AppHost déclare le client, ses redirections et la projection selon [le guide clients](clients.md). Le consommateur référence `Microsoft.AspNetCore.Authentication.OpenIdConnect` et, pour cet exemple, `Aspire.IdentityServer.Client`.

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
                    PrivateKeyJwt.CreateAssertion(client, metadata.TokenEndpoint);
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

Projette d'abord le client vers le composant sortant, par exemple `Integrations:Accounting:OAuth`. Dans un consommateur référençant le helper, la méthode suivante reprend l'échange utilisé par le sample et le smoke test. Elle suppose un `HttpClient` configuré normalement et des options liées à cette section :

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Aspire.IdentityServer.Client;

static async Task<string> RequestTokenAsync(
    HttpClient http, IdentityClientOptions client, CancellationToken cancellationToken)
{
    var metadata = await http.GetFromJsonAsync<JsonElement>(
        $"{client.Authority}/.well-known/openid-configuration", cancellationToken);
    var endpoint = metadata.GetProperty("token_endpoint").GetString()!;
    var fields = new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = client.ClientId,
        ["scope"] = client.Scopes
    };

    if (client.AuthenticationMethod == "private_key_jwt")
    {
        fields["client_assertion_type"] = PrivateKeyJwt.AssertionType;
        fields["client_assertion"] = PrivateKeyJwt.CreateAssertion(client, endpoint);
    }
    else if (client.AuthenticationMethod == "client_secret_post")
    {
        fields["client_secret"] = client.ClientSecret
            ?? throw new InvalidOperationException("Missing client secret.");
    }
    else
    {
        throw new InvalidOperationException("Machine clients require authentication.");
    }

    using var response = await http.PostAsync(
        endpoint, new FormUrlEncodedContent(fields), cancellationToken);
    response.EnsureSuccessStatusCode();
    var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    return result.GetProperty("access_token").GetString()
        ?? throw new InvalidOperationException("Missing access token.");
}
```

Transmets ensuite le token dans l'en-tête `Authorization: Bearer` de l'appel API. L'audience de l'assertion est ici `endpoint` ; l'audience du token d'accès découle des scopes et ressources enregistrés.

Cet extrait ne fournit pas de cache de métadonnées, de cache de tokens ou de stratégie de renouvellement. Le helper crée seulement une assertion. Un token d'accès peut être réutilisé pendant sa validité ; une assertion doit être recréée pour chaque demande de token.

## Duende BFF et gestion des tokens

La projection peut alimenter n'importe quelle section lue par un BFF .NET ou ses composants de gestion des tokens. Elle fournit l'authority, le client ID, les scopes et les credentials ; le BFF configure ensuite sa bibliothèque OIDC et ses clients sortants.

Cette alpha n'expose ni `AddDuendeBff()` ni adaptateur automatique pour les options des différentes versions de Duende BFF. Une clé PEM injectée ne suffit pas à activer les assertions dans cette bibliothèque. Configure ses points d'extension appropriés, y compris pour PAR s'il est actif.

Les exemples exécutés dans ce dépôt utilisent le handler OIDC ASP.NET Core standard. Ils ne constituent pas une validation d'intégration avec un package Duende BFF.

## React

Pour une architecture React + BFF, projette la clé ou le secret vers le BFF .NET, qui réalise le parcours confidentiel et les appels sortants. React utilise les routes et la session applicative du BFF.

Pour une SPA sans BFF, enregistre un client public avec PKCE. La version actuelle n'offre pas de sample React, de configuration CORS ou de réécriture d'issuer adaptée à ce parcours dans des conteneurs. N'ajoute pas une clé privée au code JavaScript pour contourner cette limite.
