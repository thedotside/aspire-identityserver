# Publier une version sur NuGet

[Documentation](README.md) · [Développement](developpement.md) · [Installation](demarrage.md)

Un tag SemVer sans préfixe déclenche la publication des trois packages sous l'organisation NuGet **TheDotSide**. Par exemple, `0.1.0-alpha.2` publie la version NuGet `0.1.0-alpha.2`, et `0.1.0` publie `0.1.0`.

## Publier depuis main

Après fusion des changements dans `main` et réussite de la CI :

```powershell
git switch main
git pull --ff-only
git tag -a 0.1.0-alpha.2 -m "Release 0.1.0-alpha.2"
git push origin 0.1.0-alpha.2
```

Choisis un nouveau numéro à chaque publication. Les tags acceptent `X.Y.Z` et `X.Y.Z-prerelease`, sans `v`, sans zéros de tête dans les nombres et sans métadonnées `+build`. Les préversions peuvent contenir plusieurs identifiants, comme `0.2.0-rc.1`.

Le tag doit pointer vers un commit appartenant à l'historique de `main`. Le pipeline contrôle cette condition avant le build. La version du tag est injectée dans le build, dans les trois packages et dans les tests de consommation ; elle remplace la version locale de `Directory.Build.props` pour cette exécution. Il n'est donc pas nécessaire de modifier les versions des scripts avant chaque tag.

Le tag préparatoire `v0.1.0-alpha.1`, antérieur au pipeline de publication, ne correspond pas au format de déclenchement.

## Chaîne de validation et de publication

Le déclencheur GitHub est dans [.github/workflows/publish.yml](../.github/workflows/publish.yml). Les étapes sont sourcées dans [pipelines/build](../pipelines/build/action.yml) et [pipelines/publish](../pipelines/publish/action.yml).

1. Le job `verify` vérifie le tag et son rattachement à `main`, restaure .NET et les dépendances, contrôle CSharpier, compile et exécute les tests de protocoles et navigateur.
2. Il construit les trois archives et les consomme dans un AppHost temporaire avec un cache NuGet isolé. Les archives vérifiées sont conservées dans l'artefact `packages-and-test-results`.
3. Le job `publish` démarre uniquement si `verify` réussit. Il récupère ces mêmes archives, vérifie leurs identifiants, versions et révision source, puis demande un jeton NuGet temporaire via OIDC.
4. Il publie `Runtime`, `Client`, puis `Hosting`. Seules les trois archives nommées explicitement sont envoyées ; les résultats de tests ne sont pas publiés sur NuGet.
5. Il attend leur indexation pendant au plus dix minutes, puis restaure les packages uniquement depuis nuget.org dans un nouveau consommateur. Celui-ci démarre le fournisseur et obtient un token réel avec `private_key_jwt`.

Une publication réussie atteste donc la validation des packages et leur consommation depuis le registre. La validation s'exécute sur Windows ; elle ne constitue pas une validation des autres systèmes.

Les publications sont sérialisées et une publication en cours n'est pas annulée par un nouveau tag. En cas d'interruption après un envoi partiel, relance le workflow GitHub existant : `--skip-duplicate` ignore les versions déjà présentes. Une version publiée reste immuable ; une correction nécessite une nouvelle version et un nouveau tag.

## Configuration initiale de Trusted Publishing

Chaque membre utilise son compte NuGet personnel. L'organisation **TheDotSide** possède les packages ; un compte Microsoft partagé n'est pas nécessaire.

Dans NuGet, le compte personnel administrateur crée une politique **Trusted Publishing** avec les paramètres suivants :

| Champ | Valeur |
| --- | --- |
| Package Owner | `TheDotSide` |
| Repository Owner | `thedotside` |
| Repository | `aspire-identityserver` |
| Workflow File | `publish.yml`, sans chemin |
| Environment | `nuget`, l'environnement GitHub du job de publication : un jeton n'est alors délivré qu'à ce job. |
| Package pattern | `TheDotSide.Aspire.IdentityServer.*` |
| Scopes | Publier de nouveaux packages et de nouvelles versions, sans droit d'unlisting. |

Dans les variables Actions du dépôt GitHub, `NUGET_USER` contient le nom du **compte personnel qui a créé la politique**, ici `Jean_Guy`. Il ne contient ni l'adresse e-mail ni le nom de l'organisation. Ce nom de profil n'est pas un secret.

Le job de publication possède `contents: read`, `actions: read` pour récupérer les archives, et `id-token: write`. L'action `NuGet/login`, épinglée par SHA, échange le jeton GitHub contre une clé NuGet temporaire immédiatement avant l'envoi. Aucune clé NuGet permanente n'est stockée dans le dépôt ou dans les secrets Actions.

Pour un dépôt privé, NuGet peut afficher une politique temporairement active pendant sept jours. Une première publication réussie permet son activation durable. La politique reste liée au compte qui l'a créée et à son appartenance à l'organisation.

Références officielles : [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), [action NuGet/login](https://github.com/NuGet/login), [organisations NuGet](https://learn.microsoft.com/en-us/nuget/nuget-org/organizations-on-nuget-org).

## Protéger la publication

Sans protection, quiconque a le droit d'écriture sur le dépôt peut publier sous **TheDotSide** en poussant un tag. Le workflow fait déjà sa part : le job `publish` s'exécute dans l'environnement GitHub `nuget`, et toutes les actions sont épinglées par SHA, avec leur version en commentaire, tenues à jour par Renovate (`helpers:pinGitHubActionDigests`). Le reste se règle dans les paramètres, par un administrateur :

1. **Environnement `nuget`** (Settings → Environments) : relecteurs requis, et règle de déploiement limitée aux tags `[0-9]*.[0-9]*.[0-9]*`. Le premier lancement crée l'environnement sans protection s'il n'existe pas encore : configure-le avant le prochain tag.
2. **Politique NuGet** : renseigne `nuget` dans le champ Environment de la politique Trusted Publishing, ci-dessus.
3. **Règles sur `main` et sur les tags** (rulesets) : PR obligatoire et CI verte pour `main`, création de tags de version réservée aux mainteneurs. GitHub ne les applique pas à un dépôt privé du plan gratuit : ce point dépend du choix de rendre le dépôt public ou de changer de plan.
4. **Jeton Renovate** : `RENOVATE_TOKEN` peut aujourd'hui écrire sur le contenu et les workflows, donc sur `main`. Une règle de PR obligatoire sur `main`, ou l'application Mend Renovate, limiterait ce jeton aux branches de PR. Ce choix documenté est à rediscuter.

## Vérifier une version publiée

Les pages de la version `0.1.0-alpha.2` sont :

- [Hosting 0.1.0-alpha.2](https://www.nuget.org/packages/TheDotSide.Aspire.IdentityServer.Hosting/0.1.0-alpha.2)
- [Runtime 0.1.0-alpha.2](https://www.nuget.org/packages/TheDotSide.Aspire.IdentityServer.Runtime/0.1.0-alpha.2)
- [Client 0.1.0-alpha.2](https://www.nuget.org/packages/TheDotSide.Aspire.IdentityServer.Client/0.1.0-alpha.2)

Pour relancer la vérification sans source locale :

```powershell
pwsh eng/package-smoke.ps1 -Version 0.1.0-alpha.2 -FromNuGet
```

## Réserver le préfixe TheDotSide.

La création de l'organisation et OIDC n'attribuent pas automatiquement le badge de préfixe réservé. La réservation de `TheDotSide.` fait l'objet d'une demande séparée à `account@nuget.org`, avec le propriétaire NuGet `TheDotSide` et les éléments identifiant l'équipe.

Après acceptation par NuGet, seuls les propriétaires autorisés peuvent créer de nouveaux packages correspondant au préfixe protégé. Le badge indique l'origine du package ; il ne certifie pas sa sécurité ni la qualité de son code. Les packages existants détenus par d'autres propriétaires ne sont pas transférés par cette réservation.

Voir [la procédure officielle et ses critères](https://learn.microsoft.com/en-us/nuget/nuget-org/id-prefix-reservation). La documentation et la présence du nom `TheDotSide.` ne constituent pas une preuve d'acceptation de la réservation.
