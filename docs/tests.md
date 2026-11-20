# Tests d'intégration des consommateurs

[Documentation](README.md) · [Intégration](integration.md) · [Référence](reference.md)

Le package facultatif `TheDotSide.Aspire.IdentityServer.Testing` aide les projets de test qui démarrent un AppHost avec `DistributedApplicationTestingBuilder`. Il obtient des jetons auprès des vrais endpoints du fournisseur : la connexion d'un utilisateur passe par authorization code + PKCE et le formulaire de connexion du fournisseur, jamais par un grant mot de passe.

```xml
<PackageReference Include="Aspire.Hosting.Testing" Version="17.0.0" />
<PackageReference Include="TheDotSide.Aspire.IdentityServer.Testing" Version="11.2.0" />
```

Les extensions sont dans l'espace de noms `Aspire.Hosting.Testing`, déjà importé par ces tests. Elles s'appliquent à une `DistributedApplication` démarrée, après que le fournisseur est sain.

## Jetons d'un utilisateur de test

```csharp
using System.Net.Http.Headers;
using Aspire.Hosting.Testing;

await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.AppHost>();
await using var app = await builder.BuildAsync();
await app.StartAsync();
await app.ResourceNotifications.WaitForResourceHealthyAsync("idp");

var alice = await app.GetUserTokensAsync("idp", "web", "alice");
using var api = app.CreateHttpClient("api", "https");
api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", alice.AccessToken);
```

`GetUserTokensAsync(provider, clientId, username, scope?)` :

1. lit le mot de passe du paramètre secret de l'utilisateur dans l'AppHost ;
2. envoie une demande d'autorisation avec PKCE `S256`, `state` et `nonce`, vers la première redirect URI du client ;
3. remplit le formulaire de connexion du fournisseur, avec son jeton antiforgery, dans une session de cookies propre à l'appel ;
4. lit le code sur la redirection vers le callback, sans appeler l'application cliente, qui n'a donc pas besoin de tourner ;
5. vérifie `state` et l'issuer de la réponse (RFC 9207), puis échange le code avec l'authentification du client : clé privée, secret ou client public.

Le résultat `IdentityTokens` contient `AccessToken`, `IdToken`, `RefreshToken` (avec `offline_access`), `Scope`, `TokenType` et `ExpiresIn`. Sans `scope`, le client demande tous les scopes qui lui sont enregistrés.

## Jetons d'un client machine et options

`GetClientCredentialsTokensAsync(provider, clientId, scope?)` demande un jeton `client_credentials`. `GetIdentityClientOptionsAsync(provider, clientId)` retourne les `IdentityClientOptions` du client, avec ses credentials résolus, comme la projection standard les fournit : pratique pour configurer un `HttpClient` de test avec `AddClientCredentialsHandler` ou `AddTokenExchangeHandler`.

Un nom de fournisseur, de client ou d'utilisateur inconnu, ou un client du mauvais type, lève `ArgumentException`. Une erreur du token endpoint lève `HttpRequestException` avec son code OAuth.

## Limites

- Le formulaire de connexion lu est celui du runtime de la même version : garde `Testing` aligné sur `Hosting`.
- Le package sert aux tests locaux contre ce fournisseur de développement, pas contre un fournisseur de production.
- Les parcours qui dépendent d'un navigateur (cookies de l'application, déconnexion, iframes) restent l'affaire de Playwright, comme dans les tests de ce dépôt.
