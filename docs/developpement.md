# Développement, tests, packages et dépannage

[Documentation](README.md) · [Démarrage](demarrage.md) · [Référence](reference.md)

## Organisation du dépôt

| Emplacement | Responsabilité |
| --- | --- |
| `src/Aspire.IdentityServer.Hosting` | API AppHost, paramètres secrets et projections. |
| `src/Aspire.IdentityServer.Server` | Runtime Duende local, package `TheDotSide.Aspire.IdentityServer.Runtime`. |
| `src/Aspire.IdentityServer.Client` | Options et helper d'assertion indépendant d'Aspire et Duende. |
| `samples/Sample.AppHost` | Deux fournisseurs, trois clients navigateur, deux clients machine. |
| `samples/Sample.Web` | Connexion et déconnexion OIDC avec les trois clients. |
| `samples/Sample.Api` | Validation de tokens de deux IDP et deux composants OAuth sortants. |
| `tests/Aspire.IdentityServer.UnitTests` | Tests unitaires : projections, règles de l'API, clés et helpers client, sans réseau ni AppHost démarré. |
| `tests/Aspire.IdentityServer.DistributedTests` | Tests distribués : protocoles, Token Exchange et navigateur contre un vrai AppHost d'exemple. |
| `eng` | Construction des packages et vérification d'un consommateur indépendant. |
| `pipelines/build` | Étapes YAML de la CI, sous forme d'action composite GitHub Actions. |
| `pipelines/renovate` | Exécution de Renovate pour proposer les mises à jour de dépendances. |
| `pipelines/publish` | Publication des archives vérifiées avec NuGet OIDC et consommation depuis le registre. |
| `docs` | Guides et exemples de configuration. |

Les versions sont centralisées dans [Directory.Packages.props](../Directory.Packages.props). Les paramètres communs et la version du package figurent dans [Directory.Build.props](../Directory.Build.props).

## Installer les outils et le hook de commit

Une fois par checkout, depuis la racine :

```powershell
pwsh eng/setup.ps1
```

Ce script restaure les outils .NET du manifeste [.config/dotnet-tools.json](../.config/dotnet-tools.json) et configure localement `core.hooksPath=.githooks`. Il refuse de remplacer silencieusement une configuration de hooks déjà différente ou un hook `pre-commit` existant. La configuration Git est locale : chaque collègue doit exécuter le script après le clonage.

Le hook [.githooks/pre-commit](../.githooks/pre-commit) appelle [eng/pre-commit.ps1](../eng/pre-commit.ps1). Il nécessite PowerShell 7 (`pwsh`) accessible dans le PATH, comme les scripts du dépôt.

Pour chaque commit, il sélectionne les fichiers C#/XML ajoutés, modifiés, copiés ou renommés dans l'index Git. Il restaure CSharpier, les formate, puis réindexe ces seuls fichiers. Les fichiers supprimés et les changements sans fichier C#/XML ne nécessitent pas de formatage.

Si un fichier sélectionné contient aussi des changements non indexés, le hook arrête le commit avant de formater. Formate ce fichier, revois les changements puis sélectionne à nouveau les lignes à committer. Le hook ne fusionne pas automatiquement les sélections partielles et ne place pas tes modifications dans un stash.

Une erreur de restauration ou une erreur de syntaxe signalée par CSharpier arrête également le commit.

## Formatage et analyse

```powershell
dotnet tool restore
dotnet csharpier format .
dotnet csharpier check .
dotnet build
```

CSharpier `1.3.0` est le formateur unique des fichiers C# et XML, dont les projets et fichiers MSBuild. [.csharpierrc.json](../.csharpierrc.json) fixe une largeur cible de 100 colonnes et des fins de ligne LF. [.csharpierignore](../.csharpierignore) exclut les sorties de build, les artefacts et le travail temporaire. Le check CI vérifie le dépôt entier, y compris quand le hook local n'a pas été installé.

Meziantou.Analyzer `3.0.315` est une `GlobalPackageReference` privée, appliquée aux projets via Central Package Management. L'analyse s'exécute au build et les avertissements restent traités comme des erreurs. Le package d'analyse n'est pas transmis aux consommateurs NuGet.

La politique se trouve dans [.editorconfig](../.editorconfig) : les règles actives par défaut sont conservées, avec comparaisons, comparateurs, culture et propagation de cancellation explicites. Les règles de nommage fichier/type (`MA0048`) et de longueur des méthodes (`MA0051`) restent des suggestions IDE, car des modèles liés partagent un fichier et les configurations fluides restent linéaires. `MA0004` reste appliquée aux bibliothèques mais est désactivée dans les applications et tests, qui n'ont pas besoin de la convention `ConfigureAwait(false)`.

Pour appliquer des corrections proposées par l'analyseur, `dotnet format analyzers` peut être utilisé sur les diagnostics concernés, puis CSharpier doit être relancé. CSharpier conserve la responsabilité du formatage ; ne lance pas `dotnet format whitespace` en parallèle.

## Exécuter les tests

Les tests distribués exigent un certificat HTTPS de développement **existant et approuvé** : les contrôles de santé des ressources passent en HTTPS. Sur un poste neuf, exécute une fois `dotnet dev-certs https --trust`, ou lance une première fois l'AppHost d'exemple avec `aspire run`, qui prépare ce certificat. Sans lui, le fixture partagé s'arrête dès qu'un contrôle de santé refuse le certificat, avec l'état et les dernières lignes de journal de chaque ressource, au lieu d'attendre la fin du délai de démarrage.

Depuis la racine :

```powershell
dotnet restore
dotnet build
pwsh tests/Aspire.IdentityServer.DistributedTests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test --no-build --report-trx --results-directory artifacts/test-results
```

Les tests utilisent [TUnit](https://tunit.dev) sur Microsoft Testing Platform, conformément à l'ADR des outils imposés (OUT-16). Le bloc `test` de [global.json](../global.json) active ce mode de `dotnet test` ; les options sont donc celles de la plateforme (`--report-trx`, `--results-directory`, `--treenode-filter`). Pour un seul projet : `dotnet test --project tests/Aspire.IdentityServer.UnitTests`.

Les deux projets suivent la séparation des tests unitaires et distribués (TST-01, TST-02) :

- `Aspire.IdentityServer.UnitTests` construit des modèles d'application en mémoire, sans réseau ni AppHost démarré ;
- `Aspire.IdentityServer.DistributedTests` partage une vraie instance de l'AppHost d'exemple et un navigateur Chromium entre toutes ses classes. Les ports sont randomisés, les ressources doivent devenir saines et chaque parcours navigateur utilise son contexte isolé.

TUnit exécute les tests en parallèle. Un test qui modifie un état global du processus, comme les diagnostics IdentityModel, porte `[NotInParallel]`.

Chaque contexte navigateur valide TLS ; `IgnoreHTTPSErrors` n'est jamais utilisé. Les attentes Playwright partagent un délai de 30 secondes. Chaque contexte enregistre aussi une trace Playwright, conservée seulement si le test échoue : elle est écrite sous `playwright/` dans le dossier de résultats et jointe au rapport. Pour l'ouvrir :

```powershell
pwsh tests/Aspire.IdentityServer.DistributedTests/bin/Debug/net10.0/playwright.ps1 show-trace <fichier.zip>
```

La CI ajoute `--hangdump --hangdump-timeout 5m` : un test bloqué produit un vidage mémoire au lieu d'occuper le runner jusqu'à son délai maximal.

| Couverture | Source |
| --- | --- |
| Issuers et clés de signature différents entre IDP | [ProtocolTests](../tests/Aspire.IdentityServer.DistributedTests/ProtocolTests.cs). |
| Tokens machine, audiences et scopes validés par l'API | ProtocolTests. |
| Plusieurs composants OAuth sortants dans une API | ProtocolTests. |
| Mauvaise signature, audience d'assertion, secret ou scope refusé | ProtocolTests. |
| Rejeu d'assertion refusé et client public refusé en client credentials | ProtocolTests. |
| Connexion/déconnexion avec clé, secret et client public | [BrowserTests](../tests/Aspire.IdentityServer.DistributedTests/BrowserTests.cs). |
| Sessions A/B coexistantes et indépendantes | BrowserTests. |
| Vérificateur PKCE absent ou incorrect | BrowserTests. |
| Clés privées conservées comme références secrètes et absentes de la configuration IDP | [ProjectionTests](../tests/Aspire.IdentityServer.UnitTests/ProjectionTests.cs). |
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

Le build local du consommateur copie dans son dossier runtime la seule fermeture du paquet Runtime, restaurée depuis NuGet, sans `deps.json` ; le smoke test le vérifie. Ce dossier contient des bibliothèques tierces et ne doit pas être redistribué comme si elles relevaient de la licence MIT du projet.

Le README est embarqué dans les packages ; les pages `docs/` restent dans le dépôt. Le README fournit un lien en ligne pour les lecteurs qui n'ont pas le checkout.

## CI et publication

Le [workflow GitHub Actions](../.github/workflows/ci.yml) déclenche la validation à l'ouverture d'une PR, à chaque nouveau commit sur celle-ci, à sa réouverture et lorsqu'elle devient prête à être revue. Toutes les branches cibles sont couvertes, y compris les PR en brouillon. La CI tourne aussi sur les pushes vers `main` et peut être lancée manuellement depuis l'onglet Actions. Un nouveau lancement sur la même PR ou branche annule le précédent encore en cours.

Les étapes du pipeline sont centralisées dans [pipelines/build/action.yml](../pipelines/build/action.yml). GitHub impose de conserver le fichier de déclenchement dans `.github/workflows` ; celui-ci ne contient que les événements, les permissions, le choix du runner et l'appel de l'action locale `./pipelines/build` après le checkout.

Le pipeline tourne sur `windows-latest` avec un délai maximal de 20 minutes. Il prépare le SDK et le certificat HTTPS, restaure les dépendances et outils, vérifie le formatage CSharpier, compile avec les analyseurs, installe Chromium, exécute les tests, construit les packages et les vérifie dans un consommateur indépendant. Il archive les résultats de tests et les packages disponibles sous `packages-and-test-results`, même si une étape précédente a échoué. Le token du workflow dispose uniquement du droit de lecture du contenu du dépôt.

Pour le runner Windows temporaire, le certificat public est exporté puis approuvé dans `LocalMachine/Root`. La confiance est vérifiée sans fenêtre interactive. Ce traitement CI ne doit pas être nécessaire pour lancer les exemples sur un poste où le certificat a déjà été approuvé.

Le workflow de CI ne publie pas sur NuGet. Un [workflow distinct](../.github/workflows/publish.yml) publie à partir d'un tag SemVer, après sa propre validation complète. Il reprend les étapes de `pipelines/build`, récupère leurs archives dans `pipelines/publish`, puis vérifie leur installation depuis nuget.org. Consulte [le guide de publication](publication.md) pour la configuration OIDC et la procédure par tag.

La présence d'une archive GitHub Actions ne signifie pas que le package existe sur nuget.org. Vérifie séparément le résultat du run et celui d'une publication.

### Mises à jour avec Renovate

Le [workflow Renovate](../.github/workflows/renovate.yml) exécute l'action [pipelines/renovate/action.yml](../pipelines/renovate/action.yml) chaque lundi à vendredi à 05:17 UTC. Il peut aussi être lancé depuis l'onglet Actions et tourne lorsqu'une modification de sa configuration arrive sur `main`.

La politique figure dans [renovate.json](../renovate.json). Elle couvre les packages NuGet centralisés, les SDK MSBuild, `global.json`, les outils .NET et les actions GitHub, y compris celles de `pipelines/`. Les versions Aspire inscrites dans le consommateur de test `eng/package-smoke.ps1` sont également détectées et regroupées avec les packages et le SDK Aspire dans une même PR. Le SDK .NET et les packages ASP.NET Core restent sur la version majeure 10.

Renovate ouvre au maximum trois PR simultanées et deux nouvelles PR par heure. Les versions majeures nécessitent une approbation dans son issue « Dependency Dashboard ». Les PR ne sont jamais fusionnées automatiquement ; elles déclenchent la CI de build et de tests habituelle.

L'exécution utilise Node.js 24 et .NET 10 sur le runner GitHub Ubuntu, sans Docker. La version de Renovate est fixée dans l'action et est elle-même suivie pour les mises à jour.

Le secret Actions `RENOVATE_TOKEN` contient un jeton GitHub limité à `thedotside/aspire-identityserver`. Il dispose des droits de lecture/écriture sur Contents, Pull requests, Issues, Commit statuses et Workflows, ainsi que de la lecture de Metadata et Dependabot alerts. Aucun droit d'organisation n'est accordé au jeton de ce pipeline. Utiliser ce jeton dédié permet aux PR de Renovate de déclencher les workflows de validation. Choisir une validité annuelle et renouveler le secret avant l'expiration du jeton, sans enregistrer sa valeur dans le dépôt ni dans les logs.

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
| Certificat HTTPS non approuvé | Exécuter `dotnet dev-certs https --trust`, accepter la demande du système, puis relancer les processus concernés. Ne désactive pas la validation TLS pour faire passer les tests. |
| `The HTTPS health check of '…' rejects the development certificate` | Même cause, détectée au démarrage des tests distribués ; approuver le certificat puis relancer. |
| `The sample AppHost did not become healthy before the startup timeout` | Lire l'état, les contrôles de santé et les journaux de chaque ressource joints au message. |
| Confirmation de certificat bloquée dans la CI | Vérifier l'étape `Prepare HTTPS` du workflow et utiliser son traitement non interactif sur le runner Windows temporaire. |
| `Executable doesn't exist` pour Chromium | Exécuter le script Playwright généré après `dotnet build`, avec `install chromium`. |
| `must use declared API scopes` | Déclarer les scopes sur une ressource du même IDP ; vérifier leur casse. |
| `Machine client ... requires authentication and API scopes only` | Ajouter une méthode de credentials ; retirer les scopes d'identité du client machine. |
| `needs an openid scope and a redirect URI` | Compléter le client interactif et vérifier le callback de son handler. |
| `Public client '…' must use PKCE` | Garder PKCE activé côté enregistrement et côté consommateur. |
| `Client authentication is already configured` | Choisir une seule méthode de credentials par client, ou créer un autre client. |
| `has no PrivateKeyPem to project` | La projection demande une clé absente ; vérifier la méthode et l'ordre de déclaration. |
| `Configuration key ... is already assigned` | Donner des sections distinctes aux projections de clients ; voir [les collisions](projections.md). |
| `invalid_client` avec clé privée | Vérifier la paire privée/JWK, le `kid`, le client ID et l'audience d'assertion, qui doit être l'issuer ; créer une assertion neuve. |
| Assertion acceptée une fois puis refusée | Ne pas réutiliser l'assertion ; le `jti` protège contre le rejeu. |
| Token refusé par une API | Vérifier issuer, audience et scope. Une assertion client n'est pas un token d'accès API. |
| Connexion mélangeant les IDP | Séparer schémas, cookies et chemins de callback ; vérifier les sections projetées. |
| Client public récupérant un ancien secret | Une projection absente n'efface pas un ancien `appsettings` ; retirer le credential du fichier et utiliser la méthode `none`. |
| Le PEM est un chemin ou contient les caractères littéraux `\n` | Fournir le contenu PEM réel ; le binding ne lit pas un fichier ni ne convertit une chaîne arbitraire de caractères échappés. |
| Consommateur en attente | Vérifier que l'IDP est sain via `/health` et ses logs, sans y afficher les credentials. |
| Publication Aspire refusée | Limite volontaire : déclarer le fournisseur dans `if (builder.ExecutionContext.IsRunMode)`, voir [le démarrage](demarrage.md#environnement-de-lapphost-et-publication). |
| `runs only in an AppHost started in Development or Testing` | Lancer l'AppHost avec son profil, ou définir `DOTNET_ENVIRONMENT=Development`. |
| Session perdue après redémarrage | État et Data Protection éphémères, comportement prévu. |

## Licence et contributions

Le code original est MIT. Duende et les autres dépendances gardent leurs licences respectives. Les archives du projet contiennent seulement ses assemblies ; les droits de distribution d'une application assemblée avec les dépendances relèvent de leurs propres termes. Consulte [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).

Préserve les contrôles réels des protocoles, les endpoints différés et les références de paramètres secrets. Les clés, secrets, tokens et traces de test ne doivent pas entrer dans les fichiers suivis. Les dossiers `artifacts`, `work`, `bin` et `obj` sont ignorés.

L'API de configuration des certificats Aspire utilisée par Hosting est expérimentale (`ASPIRECERTIFICATES001`). Après une mise à jour Aspire ou une modification de packaging, exécute build, tests, pack et smoke. Un temps de démarrage mesuré pour l'AppHost ne constitue pas un benchmark comparatif avec Keycloak.
