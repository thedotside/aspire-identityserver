# Développement, tests, packages et dépannage

[Documentation](README.md) · [Démarrage](demarrage.md) · [Référence](reference.md)

## Organisation du dépôt

| Emplacement | Responsabilité |
| --- | --- |
| `src/Aspire.IdentityServer.Hosting` | API AppHost, paramètres secrets et projections. |
| `src/Aspire.IdentityServer.Server` | Runtime Duende local, package `Aspire.IdentityServer.Runtime`. |
| `src/Aspire.IdentityServer.Client` | Options et helper d'assertion indépendant d'Aspire et Duende. |
| `samples/Sample.AppHost` | Deux fournisseurs, trois clients navigateur, deux clients machine. |
| `samples/Sample.Web` | Connexion et déconnexion OIDC avec les trois clients. |
| `samples/Sample.Api` | Validation de tokens de deux IDP et deux composants OAuth sortants. |
| `tests/Aspire.IdentityServer.Tests` | Tests de protocoles, navigateurs et projections. |
| `eng` | Construction des packages et vérification d'un consommateur indépendant. |
| `docs` | Guides et exemples de configuration. |

Les versions sont centralisées dans [Directory.Packages.props](../Directory.Packages.props). Les paramètres communs et la version du package figurent dans [Directory.Build.props](../Directory.Build.props).

## Exécuter les tests

Depuis la racine, après préparation du certificat de développement :

```powershell
dotnet restore
dotnet build
pwsh tests/Aspire.IdentityServer.Tests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --no-build --logger trx --results-directory artifacts/test-results
```

Les tests de protocoles et navigateurs partagent une vraie instance d'AppHost. Les ports sont randomisés, les ressources doivent devenir saines et chaque parcours navigateur utilise son contexte isolé.

| Couverture | Source |
| --- | --- |
| Issuers et clés de signature différents entre IDP | [ProtocolTests](../tests/Aspire.IdentityServer.Tests/ProtocolTests.cs). |
| Tokens machine, audiences et scopes validés par l'API | ProtocolTests. |
| Plusieurs composants OAuth sortants dans une API | ProtocolTests. |
| Mauvaise signature, audience d'assertion, secret ou scope refusé | ProtocolTests. |
| Rejeu d'assertion refusé et client public refusé en client credentials | ProtocolTests. |
| Connexion/déconnexion avec clé, secret et client public | [BrowserTests](../tests/Aspire.IdentityServer.Tests/BrowserTests.cs). |
| Sessions A/B coexistantes et indépendantes | BrowserTests. |
| Vérificateur PKCE absent ou incorrect | BrowserTests. |
| Clés privées conservées comme références secrètes et absentes de la configuration IDP | [ProjectionTests](../tests/Aspire.IdentityServer.Tests/ProjectionTests.cs). |
| Scopes inconnus, champs absents et collisions de projections | ProjectionTests. |

La validation locale Windows consignée le **17 octobre 2026** a produit **21 tests réussis, zéro échec**. Ce résultat est historique ; exécute les commandes ci-dessus pour une nouvelle révision. Il ne prouve pas la compatibilité d'un BFF tiers, d'un cluster ou d'un autre OS.

## Construire et vérifier les packages

```powershell
pwsh eng/pack.ps1
pwsh eng/package-smoke.ps1
```

`pack.ps1` construit en Release les trois archives dans `artifacts/packages` et vérifie qu'elles ne contiennent pas d'assemblies tierces. Les dépendances sont des références NuGet normales, restaurées depuis leurs packages d'origine.

`package-smoke.ps1` crée un AppHost temporaire et un cache NuGet isolé. Il consomme les vrais packages, démarre un fournisseur et obtient un token avec une assertion JWT. Le succès contient :

```text
PACKAGE_SMOKE_OK: packaged provider started and authenticated the packaged client helper.
```

Les scripts acceptent le même paramètre `-Version`. Pour vérifier une version différente, utilise la même valeur dans les deux commandes. Ne remplace pas une version déjà publiée : une version NuGet publiée est destinée à rester immuable.

Le build local du consommateur copie les dépendances restaurées dans son dossier runtime. Ce dossier contient des bibliothèques tierces et ne doit pas être redistribué comme si elles relevaient de la licence MIT du projet.

Le README est embarqué dans les packages ; les pages `docs/` restent dans le dépôt. Le README fournit un lien en ligne pour les lecteurs qui n'ont pas le checkout.

## CI et publication

Le [workflow](../.github/workflows/ci.yml) tourne sur `windows-latest` et prévoit : SDK, certificat HTTPS, restore, build, installation Chromium, tests, pack, smoke et archivage des résultats/packages.

Pour le runner Windows temporaire, le certificat public est exporté puis approuvé dans `LocalMachine/Root`. La confiance est vérifiée sans fenêtre interactive. Ce traitement CI ne doit pas être nécessaire pour lancer les exemples sur un poste où le certificat a déjà été approuvé.

Le workflow ne publie pas automatiquement sur NuGet. La présence d'une archive GitHub Actions ne signifie pas que le package existe sur nuget.org. Vérifie séparément le résultat du run et celui d'une publication.

### Préparation d'une publication

- [ ] Choisir la version et la mettre en cohérence dans les propriétés/scripts utilisés.
- [ ] Exécuter build et tests sur la révision à publier.
- [ ] Exécuter pack puis le smoke test sur les archives exactes.
- [ ] Vérifier les noms, versions, dépendances, README et contenu des packages.
- [ ] Vérifier les liens de documentation et les [notices tierces](../THIRD-PARTY-NOTICES.md).
- [ ] Choisir le propriétaire individuel ou organisation NuGet et disposer de ses droits de publication.
- [ ] Publier avec le mécanisme d'authentification du registre, sans credentials dans les fichiers suivis.
- [ ] Vérifier la validation/indexation et l'installation depuis le registre avant d'annoncer la disponibilité.

Cette checklist n'affirme pas qu'une publication a été effectuée. Les identités GitHub et NuGet sont distinctes ; transférer un dépôt ne transfère pas automatiquement la propriété des packages.

## Dépannage

| Symptôme | Cause probable / vérification |
| --- | --- |
| `The bundled identity server is missing` | Construire l'AppHost ; vérifier `bin/.../identityserver/Aspire.IdentityServer.Server.dll`. En référence projet, vérifier l'import du `.targets`. Si `RuntimeDirectory` est fourni, vérifier son chemin et ses dépendances. |
| Certificat HTTPS non approuvé | Exécuter `dotnet dev-certs https --trust`, accepter la demande Windows, puis relancer les processus concernés. Ne désactive pas la validation TLS pour faire passer les tests. |
| Confirmation de certificat bloquée dans la CI | Vérifier l'étape `Prepare HTTPS` du workflow et utiliser son traitement non interactif sur le runner Windows temporaire. |
| `Executable doesn't exist` pour Chromium | Exécuter le script Playwright généré après `dotnet build`, avec `install chromium`. |
| `must use declared API scopes` | Déclarer les scopes sur une ressource du même IDP ; vérifier leur casse. |
| `Machine client ... requires authentication and API scopes only` | Ajouter une méthode de credentials ; retirer les scopes d'identité du client machine. |
| `needs an openid scope and a redirect URI` | Compléter le client interactif et vérifier le callback de son handler. |
| `Public clients must use PKCE` | Garder PKCE activé côté enregistrement et côté consommateur. |
| `Client authentication is already configured` | Choisir une seule méthode de credentials par client, ou créer un autre client. |
| `has no PrivateKeyPem to project` | La projection demande une clé absente ; vérifier la méthode et l'ordre de déclaration. |
| `Configuration key ... is already assigned` | Donner des sections distinctes aux projections de clients ; voir [les collisions](projections.md). |
| `invalid_client` avec clé privée | Vérifier la paire privée/JWK, le `kid`, le client ID et l'audience d'assertion ; créer une assertion neuve. |
| Assertion acceptée une fois puis refusée | Ne pas réutiliser l'assertion ; le `jti` protège contre le rejeu. |
| Token refusé par une API | Vérifier issuer, audience et scope. Une assertion client n'est pas un token d'accès API. |
| Connexion mélangeant les IDP | Séparer schémas, cookies et chemins de callback ; vérifier les sections projetées. |
| Client public récupérant un ancien secret | Une projection absente n'efface pas un ancien `appsettings` ; retirer le credential du fichier et utiliser la méthode `none`. |
| Le PEM est un chemin ou contient les caractères littéraux `\n` | Fournir le contenu PEM réel ; le binding ne lit pas un fichier ni ne convertit une chaîne arbitraire de caractères échappés. |
| Consommateur en attente | Vérifier que l'IDP est sain via `/health` et ses logs, sans y afficher les credentials. |
| Publication Aspire refusée | Limite volontaire de cette alpha ; voir [Kubernetes](kubernetes.md). |
| Session perdue après redémarrage | État et Data Protection éphémères, comportement prévu. |

## Licence et contributions

Le code original est MIT. Duende et les autres dépendances gardent leurs licences respectives. Les archives du projet contiennent seulement ses assemblies ; les droits de distribution d'une application assemblée avec les dépendances relèvent de leurs propres termes. Consulte [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

Préserve les contrôles réels des protocoles, les endpoints différés et les références de paramètres secrets. Les clés, secrets, tokens et traces de test ne doivent pas entrer dans les fichiers suivis. Les dossiers `artifacts`, `work`, `bin` et `obj` sont ignorés.

L'API de configuration des certificats Aspire utilisée par Hosting est expérimentale (`ASPIRECERTIFICATES001`). Après une mise à jour Aspire ou une modification de packaging, exécute build, tests, pack et smoke. Un temps de démarrage mesuré pour l'AppHost ne constitue pas un benchmark comparatif avec Keycloak.
