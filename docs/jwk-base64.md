# Clés privées RSA JWK Base64 : BFF et CC Xp/Capa

[Documentation](README.md) · [Clients](clients.md) · [Projections](projections.md) · [Kubernetes](kubernetes.md)

Ce support est ajouté dans la version `0.1.0-alpha.2`. Les packages `0.1.0-alpha.1` exposent seulement `PrivateKeyPem`. Installe la nouvelle version avant de retirer l'adaptation du template.

## Contrat exact

`PrivateKeyBase64 = Base64(UTF8(JSON JWK privée))`.

Le JSON est directement un objet contenant `kty`, `kid`, `n`, `e`, `d`, `p`, `q`, `dp`, `dq`, `qi`. Ce n'est ni un JWKS (`keys`), ni un PEM encodé, ni une enveloppe contenant une propriété `privateKey`. Les composantes RSA utilisent Base64URL sans padding. Le JSON complet utilise Base64 standard, avec son padding éventuel.

Les clés générées sont RSA 2048 bits, avec `alg: "RS256"` et `use: "sig"`. Les clés importées doivent faire au moins 2048 bits et contenir toutes les composantes privées. `alg` et `use` sont facultatifs à l'import ; s'ils sont présents, ils doivent valoir `RS256` et `sig`.

Le `kid` généré est le SHA-256 hexadécimal de la clé publique SubjectPublicKeyInfo. Un `kid` importé est conservé. Le helper utilise le `kid` de la JWK ; si `IdentityClientOptions.KeyId` est également renseigné, il doit correspondre exactement, y compris la casse.

## Exemples AppHost : BFF et Client Credentials

Ces extraits supposent des ressources consommatrices Aspire `bff`, `xp`, `capa` déjà déclarées avec leur endpoint HTTPS. Les chemins de configuration sont des exemples ; utilise ceux que lisent tes applications.

```csharp
using Aspire.IdentityServer.Hosting;

var idp = builder.AddIdentityServer("idp");
var xpApi = idp.AddApiResource("xp-api").WithScopes("xp.read");
var capaApi = idp.AddApiResource("capa-api").WithScopes("capa.read");

var bffClient = idp.AddAuthorizationCodeClient("bff")
    .WithGeneratedBase64PrivateKeyJwt()
    .RequirePkce()
    .WithScopes("openid", "profile", "xp.read")
    .WithRedirectUri(bff.GetEndpoint("https"), "/signin-oidc")
    .WithPostLogoutRedirectUri(bff.GetEndpoint("https"), "/signout-callback-oidc");
var ccXp = idp.AddClientCredentialsClient("cc-xp")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("xp.read");
var ccCapa = idp.AddClientCredentialsClient("cc-capa")
    .WithGeneratedBase64PrivateKeyJwt()
    .WithScopes("capa.read");

bff.WithIdentityClient(bffClient, "Authentication:Bff")
    .WithIdentityClient(ccXp, "Integrations:Xp:OAuth");
xp.WithIdentityClient(ccCapa, "Integrations:Capa:OAuth");
xp.WithIdentityResource(xpApi, "Authentication:Inbound");
capa.WithIdentityResource(capaApi, "Authentication:Inbound");
```

La projection standard injecte `Authority`, `ClientId`, `AuthenticationMethod`, `Scopes`, `PrivateKeyBase64`, `KeyId` et, si applicable, `TokenExchangeAudience`. L'autorité reste une référence d'endpoint différée, la clé reste un paramètre secret Aspire et le consommateur attend l'IdP. Seuls `kty`, `kid`, `alg`, `use`, `n`, `e` sont enregistrés dans la configuration de l'IdP.

Pour valider les tokens entrants, conserve séparément les `IdentityApiResource` retournées par `AddApiResource` et projette-les avec `WithIdentityResource`. Une API entrante reçoit uniquement l'autorité et l'audience, sans clé privée client.

## Importer la valeur applicative

```csharp
var value = builder.Configuration["DevelopmentKeys:CcCapaPrivateKeyBase64"]
    ?? throw new InvalidOperationException("Missing development signing key.");
var ccCapa = idp.AddClientCredentialsClient("cc-capa-imported")
    .WithBase64PrivateKeyJwt(value)
    .WithScopes("capa.read");
xp.WithIdentityClient(ccCapa, "Integrations:Capa:OAuth");
```

La surcharge valide la clé et déduit la JWK publique. Conserve la valeur privée dans une configuration locale secrète, hors des fichiers suivis par Git.

## Utiliser un paramètre secret existant

```csharp
var privateKey = builder.AddParameter("cc-capa-private-jwk-base64", secret: true);
var publicJwk = builder.Configuration["DevelopmentKeys:CcCapaPublicJwk"]
    ?? throw new InvalidOperationException("Missing public signing key.");
var ccCapa = idp.AddClientCredentialsClient("cc-capa-parameter")
    .WithBase64PrivateKeyJwt(privateKey, publicJwk)
    .WithScopes("capa.read");
xp.WithIdentityClient(ccCapa, "Integrations:Capa:OAuth");
```

Le paramètre `Parameters:cc-capa-private-jwk-base64` contient directement la valeur applicative. La JWK publique est du JSON texte, contenant `kty`, `kid`, `n`, `e` et éventuellement `alg`/`use`. Elle ne contient aucune composante privée.

Cette surcharge ne résout pas le secret pendant l'enregistrement. La paire doit correspondre : le helper contrôle le `kid` au moment de signer, et l'IdP vérifie la signature avec la clé publique enregistrée. Deux clés différentes portant le même `kid` sont donc refusées par l'IdP.

## Consommateurs : retirer la conversion vers PEM

Le binding devient direct, sans décodage ou conversion dans le template :

```csharp
using Aspire.IdentityServer.Client;

var bffClient = builder.Configuration.GetSection("Authentication:Bff")
    .Get<IdentityClientOptions>()!;
var ccXp = builder.Configuration.GetSection("Integrations:Xp:OAuth")
    .Get<IdentityClientOptions>()!;
var ccCapa = builder.Configuration.GetSection("Integrations:Capa:OAuth")
    .Get<IdentityClientOptions>()!;

// Dans chaque demande de token, avec l'issuer découvert :
var assertion = PrivateKeyJwt.CreateAssertion(ccCapa, issuer);
```

Pour le BFF, l'événement OIDC `OnAuthorizationCodeReceived` appelle le même helper avec `bffClient`, comme dans [l'intégration ASP.NET Core](integration.md). Les appels CC Xp/Capa utilisent `client_assertion_type = PrivateKeyJwt.AssertionType` et une assertion fraîche à chaque demande. L'issuer et les endpoints doivent provenir des métadonnées de l'IdP concerné. PAR, s'il est actif dans la bibliothèque BFF, doit également être authentifié ; aucun adaptateur Duende BFF automatique n'est ajouté.

La projection personnalisée reste disponible :

```csharp
xp.WithIdentityClient(ccCapa, p => p
    .Authority("Capa:OAuth:Authority")
    .ClientId("Capa:OAuth:ClientId")
    .AuthenticationMethod("Capa:OAuth:AuthenticationMethod")
    .Scopes("Capa:OAuth:Scopes")
    .PrivateKeyBase64("Capa:OAuth:PrivateKeyBase64")
    .KeyId("Capa:OAuth:KeyId"));
```

Supprime les anciennes valeurs `PrivateKeyPem` des sections migrées : fournir simultanément PEM et Base64 est refusé. Les API PEM et `client_secret_post` restent utilisables. Le comportement du Token Exchange reste identique et son handler demeure activé uniquement sur les clients HTTP explicitement sélectionnés.

## Kubernetes : conserver les deux niveaux d'encodage

La valeur de l'application est déjà du Base64. Dans `Secret.stringData`, fournis cette valeur directement :

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: cc-capa-oauth
type: Opaque
stringData:
  private-key-base64: "<Base64(UTF8(JSON-JWK-privee))>"
```

Avec `Secret.data`, encode une seconde fois la valeur applicative :

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: cc-capa-oauth
type: Opaque
data:
  private-key-base64: "<Base64(UTF8(valeur-applicative-Base64))>"
```

Dans les deux cas, Kubernetes écrit exactement la même valeur applicative dans le fichier monté. Préfère un volume à une variable d'environnement, comme dans [le guide Kubernetes](kubernetes.md#exemple-yaml--bff-ou-application-web-avec-clé-privée) : le nom du fichier est la clé de configuration lue par `AddKeyPerFile` :

```yaml
volumes:
  - name: oidc-secrets
    secret:
      secretName: cc-capa-oauth
      defaultMode: 0400
      items:
        - key: private-key-base64
          path: Integrations__Capa__OAuth__PrivateKeyBase64
```

Pour le BFF ou CC Xp, utilise respectivement `Authentication__Bff__PrivateKeyBase64` ou `Integrations__Xp__OAuth__PrivateKeyBase64`. Une variable injectée par `secretKeyRef` sous le même nom reste possible si l'environnement l'impose. Fournis aussi les champs publics de la section, dont `AuthenticationMethod: private_key_jwt` et le `KeyId` correspondant. Les marqueurs YAML sont à remplacer hors du dépôt ; ne versionne aucune valeur privée, même encodée.

L'API Kubernetes convertit `stringData` en `data` à l'écriture ; le décodage de `data` retire uniquement l'encodage Kubernetes. Voir la [documentation officielle des Secrets](https://kubernetes.io/docs/concepts/configuration/secret/). Cela concerne les consommateurs avec un IdP accessible et adapté à leur environnement ; le fournisseur local de ce dépôt reste limité au développement et aux tests.

## Vérifier les packages localement avant de migrer le template

Depuis ce dépôt, ces commandes construisent et vérifient les packages locaux :

```powershell
pwsh eng/pack.ps1 -Version 0.1.0-alpha.2
pwsh eng/package-smoke.ps1 -Version 0.1.0-alpha.2
```

Dans le template, remplace les deux références `0.1.0-alpha.1` par `0.1.0-alpha.2`, puis utilise les appels ci-dessus. Avec Central Package Management, modifie `Directory.Packages.props`. Sinon, après disponibilité sur NuGet :

```powershell
dotnet add <AppHost.csproj> package TheDotSide.Aspire.IdentityServer.Hosting --version 0.1.0-alpha.2 --source https://api.nuget.org/v3/index.json
dotnet add <Consommateur.csproj> package TheDotSide.Aspire.IdentityServer.Client --version 0.1.0-alpha.2 --source https://api.nuget.org/v3/index.json
```

Pour utiliser les archives locales avant publication, remplace la source par `C:/dev/aspire-identityserver/artifacts/packages`. Le Runtime est une dépendance NuGet transitive de Hosting. Le dépôt du template reste inchangé par cette tâche.
