# Configuration Kubernetes et limites de déploiement

[Documentation](README.md) · [Projections](projections.md) · [Intégration](integration.md)

## État actuel

Le fournisseur `TheDotSide.Aspire.IdentityServer.Runtime` est **exclu de tout déploiement**, dans cette version comme dans les suivantes. C'est un outil de développement et de test : état en mémoire, clé de signature éphémère, écoute loopback, et licence Duende limitée aux tests et à l'évaluation internes. Cette page ne concerne que les **applications consommatrices**, qui parlent en cluster à un fournisseur réel.

| Scénario | Prise en charge |
| --- | --- |
| IDP et consommateurs .NET locaux, lancés par Aspire | Seul usage prévu du fournisseur. |
| Générer un déploiement de cet IDP avec Aspire | Refusé par `AddIdentityServer()` en mode publication. |
| Exécuter ce runtime comme IDP dans un cluster | Exclu : issuer et écoute loopback, environnement Development/Testing uniquement. |
| Donner les mêmes clés de configuration à un BFF/API sur Kubernetes | Possible avec l'environnement .NET standard et un IDP adapté, accessible depuis cet environnement. Exemple manuel ci-dessous. |
| Transformer automatiquement nos paramètres Aspire en ConfigMap/Secret Kubernetes | Non implémenté dans ce package. |

Un `ParameterResource` Aspire marqué secret n'est pas un objet Kubernetes `Secret`. Le package ne crée pas de chart Helm, de manifests ni de ressources dans un cluster.

Le YAML de cette page décrit la configuration d'un **consommateur**. Il n'est pas une sortie d'`aspire publish` et ne rend pas déployable le fournisseur local.

## Le paquet Client face à un fournisseur réel

`TheDotSide.Aspire.IdentityServer.Client` ne dépend d'aucun moteur. Ses helpers suivent des profils standard : assertions `private_key_jwt` RS256 et Token Exchange RFC 8693. Ils fonctionnent donc contre un fournisseur réel conforme, ce que montre cette page. Le paquet reste en préversion : valide-le contre ton fournisseur et ses exigences d'assertion avant de l'utiliser hors développement.

## Contrat de configuration réutilisable

L'application garde la même lecture :

```csharp
var client = builder.Configuration.GetSection("Authentication:IdpA")
    .Get<IdentityClientOptions>();
```

| Configuration .NET | Environnement du conteneur | Source dans l'exemple |
| --- | --- | --- |
| `Authentication:IdpA:Authority` | `Authentication__IdpA__Authority` | ConfigMap. |
| `Authentication:IdpA:ClientId` | `Authentication__IdpA__ClientId` | ConfigMap. |
| `Authentication:IdpA:AuthenticationMethod` | `Authentication__IdpA__AuthenticationMethod` | ConfigMap. |
| `Authentication:IdpA:Scopes` | `Authentication__IdpA__Scopes` | ConfigMap, chaîne séparée par des espaces. |
| `Authentication:IdpA:KeyId` | `Authentication__IdpA__KeyId` | ConfigMap, doit correspondre au JWK public enregistré. |
| `Authentication:IdpA:PrivateKeyPem` | `Authentication__IdpA__PrivateKeyPem` | Secret existant. |
| `Authentication:IdpA:PrivateKeyBase64` | `Authentication__IdpA__PrivateKeyBase64` | Depuis `0.1.0-alpha.2` : Secret existant contenant la valeur JWK Base64, sans `PrivateKeyPem`. |

Pour une JWK privée Base64, `Secret.stringData` reçoit directement `Base64(UTF8(JSON JWK privée))`. `Secret.data` reçoit un encodage Base64 supplémentaire de cette valeur applicative. Le [guide BFF et CC Xp/Capa](jwk-base64.md#kubernetes--conserver-les-deux-niveaux-dencodage) fournit les deux YAML et les variables d'environnement exactes.

## Exemple YAML : BFF ou application web avec clé privée

Le fichier complet est [examples/kubernetes/web.yaml](examples/kubernetes/web.yaml). Avant de l'utiliser :

1. Remplace l'authority fictive par celle d'un fournisseur adapté à l'environnement.
2. Enregistre le client `web`, son JWK public, ses scopes et ses callbacks auprès de cet IDP.
3. Remplace le `kid` d'exemple par celui de la paire de clés enregistrée.
4. Utilise une image réelle de ton application, configurée pour lire cette section et authentifier ses demandes.
5. Fournis dans le même namespace un Secret `web-oidc` avec la clé `private-key.pem`.

Le Deployment importe les champs publics de la ConfigMap et **monte** la clé privée en fichier, plutôt que de la placer dans une variable d'environnement. Une variable est visible des processus enfants, des vidages mémoire et de `/proc/<pid>/environ` ; un volume en lecture seule limite cette exposition. Le nom du fichier est la clé de configuration, avec `__` comme séparateur :

```yaml
envFrom:
  - configMapRef:
      name: web-oidc-config
volumeMounts:
  - name: oidc-secrets
    mountPath: /run/secrets/oidc
    readOnly: true
# ...
volumes:
  - name: oidc-secrets
    secret:
      secretName: web-oidc
      defaultMode: 0400
      items:
        - key: private-key.pem
          path: Authentication__IdpA__PrivateKeyPem
```

L'application lit ce dossier avec le provider [Key-per-file](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/#key-per-file-configuration-provider) (package `Microsoft.Extensions.Configuration.KeyPerFile`), puis lie la section comme en local :

```csharp
builder.Configuration.AddKeyPerFile("/run/secrets/oidc", optional: true);
var client = builder.Configuration.GetSection("Authentication:IdpA")
    .Get<IdentityClientOptions>();
```

Pour un environnement de démonstration, cette commande crée le Secret depuis un fichier local existant, hors du dépôt. Remplace le chemin et le namespace ; ne mets pas le contenu PEM dans la ligne de commande :

```powershell
$privateKeyPath = 'C:/chemin-local/non-versionne/web-private-key.pem'
kubectl -n mon-namespace create secret generic web-oidc --from-file="private-key.pem=$privateKeyPath"
```

La configuration attend le contenu de la clé, pas son chemin : Key-per-file lit le fichier et place son contenu PEM, retours à la ligne compris, dans `Authentication:IdpA:PrivateKeyPem`. Si ton environnement impose des variables, `secretKeyRef` reste possible avec le nom `Authentication__IdpA__PrivateKeyPem`, au prix de l'exposition décrite ci-dessus.

Le fichier YAML ne contient aucune clé privée ni ressource Secret avec un faux PEM. Il reste un modèle de configuration : aucune validation de déploiement sur un cluster n'est revendiquée. Le gestionnaire de secrets et les droits d'accès restent ceux de l'environnement d'accueil ; un Secret Kubernetes n'est pas une garantie d'encryption au repos par lui-même.

## Variante avec secret client

Dans la ConfigMap, définis `Authentication__IdpA__AuthenticationMethod: "client_secret_post"`. N'ajoute pas `KeyId` pour ce cas. Monte le secret sous le nom de sa clé de configuration :

```yaml
items:
  - key: client-secret
    path: Authentication__IdpA__ClientSecret
```

L'IDP doit avoir un client enregistré avec ce secret, et le consommateur doit utiliser la méthode correspondante.

## Client public et API entrante

Pour un client public, conserve `AuthenticationMethod: "none"` et aucun credential. PKCE et les callbacks restent à configurer dans la bibliothèque OIDC. Une SPA React ne lit pas automatiquement les variables d'environnement du serveur à l'exécution : son mécanisme de configuration doit être traité dans son propre projet.

Pour une API qui valide seulement les tokens, la configuration correspondant à `WithIdentityApiResource` se limite à l'authority et l'audience :

```yaml
env:
  - name: Authentication__Inbound__Authority
    value: "https://identity.example.invalid"
  - name: Authentication__Inbound__Audience
    value: "orders-api-a"
```

L'API découvre les clés publiques de l'IDP. Elle n'a pas besoin de clé privée client pour valider des tokens entrants.

## Deux IDP et plusieurs composants

Pour un second IDP, ajoute une section `Authentication__IdpB__...` et son Secret propre. Pour un client sortant, utilise par exemple `Integrations__Accounting__OAuth__...`. Les noms suivent les mêmes règles de projection que dans l'AppHost.

Le même identifiant de client peut exister chez deux fournisseurs. Chaque schéma applicatif doit conserver l'authority, les credentials et les callbacks de son fournisseur.

## Pourquoi les adresses locales ne se transposent pas directement

Dans un Pod, `localhost` désigne le réseau de ce Pod. Une URL locale allouée par l'AppHost ne permet pas à un autre Pod de joindre l'IDP sur la machine de développement.

Pour un déploiement OIDC, l'issuer et les métadonnées doivent être cohérents pour le navigateur et les services, avec une URL stable et un certificat approuvé. Les callbacks doivent aussi utiliser l'URL déployée de l'application.

Le runtime de ce projet vérifie explicitement que l'issuer est loopback et utilise HTTPS. Il écoute en code sur la seule interface loopback : `ASPNETCORE_URLS` et les variables `*_PORTS` ne peuvent pas l'exposer, une section `Kestrel:Endpoints` est refusée, et il s'arrête si une adresse effective n'est pas locale. Cette alpha n'offre pas de réécriture d'issuer réseau, de terminaison TLS/Ingress ou de persistance de clés pour ce scénario. Modifier seulement les variables d'environnement ne suffit pas à fournir ce support.

## Sources

- [Blocage du mode publication](../src/Aspire.IdentityServer.Hosting/IdentityServerExtensions.cs).
- [Contraintes du runtime](../src/Aspire.IdentityServer.Server/Program.cs).
- [Configuration .NET et environnement](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0).
- [ConfigMap vers environnement](https://kubernetes.io/docs/tasks/configure-pod-container/configure-pod-configmap/).
- [Secret vers environnement ou fichier](https://kubernetes.io/docs/tasks/inject-data-application/distribute-credentials-secure/).
- [Propriétés et gestion des Secrets](https://kubernetes.io/docs/concepts/configuration/secret/).
