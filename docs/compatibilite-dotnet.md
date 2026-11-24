# Compatibilité .NET 10 et .NET 11

[Documentation](README.md) · [Installation](demarrage.md) · [Publication](publication.md)

La première version stable `10.0.0` reprend les fonctionnalités de `0.1.0-alpha.5` pour .NET 10. La version stable `11.0.0` conserve .NET 10 et ajoute un ciblage explicite .NET 11. Le numéro majeur sert de repère de famille ; la compatibilité dépend des cibles et des validations ci-dessous.

## Packages et SDK

| Élément | .NET 10 | .NET 11 |
| --- | --- | --- |
| Packages IdentityServer 11.4.0 (en développement) | Asset `net10.0` | Asset `net11.0` |
| SDK du consommateur | SDK 10 | SDK 11 |
| Framework du fournisseur lancé | .NET / ASP.NET Core 10 | .NET / ASP.NET Core 11 |
| Authentification ASP.NET Core des exemples | 10.0.15 | 11.0.0 |
| Aspire minimum | 17.0.0 | 17.0.0 |
| Contrat Hosting/Runtime | 4 | 4 |

Un seul identifiant et une seule version de package couvrent les deux frameworks : NuGet choisit l'asset correspondant. Garde Hosting, Runtime et le helper Client facultatif alignés. Le fournisseur reste un processus indépendant ; son fichier `runtimeconfig.json` correspond au framework du consommateur, même si les deux runtimes sont installés. Le mécanisme de roll-forward majeur reste disponible pour les frameworks ultérieurs ; il ne constitue pas une validation de ces versions futures.

Construire ce dépôt nécessite le SDK [global.json](../global.json), actuellement 11.0.100. Exécuter les deux cibles de tests demande aussi le runtime ASP.NET Core 10. Les vérifications de packages demandent les SDK 10 et 11 : leurs consommateurs temporaires possèdent chacun leur propre `global.json`, avec un cache NuGet isolé.

```powershell
dotnet build
dotnet test
pwsh eng/pack.ps1
pwsh eng/package-smoke.ps1
```

Pour un seul framework :

```powershell
dotnet test --framework net11.0
dotnet run --project samples/Sample.AppHost --framework net11.0
pwsh eng/package-smoke.ps1 -TargetFramework net11.0
```

Remplace `net11.0` par `net10.0` pour l'autre cible. La CI Windows/Linux exécute les deux suites et vérifie les packages dans les deux familles de SDK.

La validation locale Windows du 11 novembre 2026 a réussi : 400 tests au total, soit 117 tests unitaires et 83 distribués par framework, sans test ignoré. Les trois archives et leurs symboles ont été construits ; les consommateurs isolés ont démarré le fournisseur et obtenu des tokens avec SDK 10/.NET 10 puis SDK 11/.NET 11. Cela ne constitue pas une validation macOS ; les résultats Linux sont à lire dans l'exécution CI de la PR.

## Moteur Duende

Le moteur reste Duende IdentityServer 8.0.10, avec ses dépendances et sa licence d'origine. La [politique de support publiée par Duende](https://docs.duendesoftware.com/general/support-and-issues/#supported-versions) annonce .NET 10 pour IdentityServer 8.0. Les tests de cette intégration sous .NET 11 ne doivent pas être présentés comme une annonce de support ou une certification de Duende sous .NET 11.

Cette distinction vaut aussi pour le statut stable : il concerne notre API et le périmètre de développement et de tests locaux. L'état reste en mémoire, l'écoute limitée au loopback et l'AppHost doit être en `Development` ou `Testing` ; la publication Aspire et la production restent refusées.

## Audit des changements .NET 11

L'audit utilise les [ruptures .NET 11](https://learn.microsoft.com/dotnet/core/compatibility/11) et [ASP.NET Core 11](https://learn.microsoft.com/aspnet/core/breaking-changes/11/overview), en complément des compilations et échanges réels.

| Changement | Conséquence pour cette intégration |
| --- | --- |
| SDK, compilateur et TFM | SDK 11 pour construire les assets `net11.0`, sans retirer `net10.0`. Les avertissements restent des erreurs. |
| Sélection du runtime | Fichier de démarrage distinct pour .NET 11 : une assembly `net11.0` ne doit pas être lancée avec un fichier demandant .NET 10. Le smoke test vérifie aussi l'asset Runtime choisi par NuGet. |
| Projets multicibles lancés par Aspire | Le sample fixe `TargetFramework` avec `WithBuildEnvironment` pour construire et lancer l'API et le web sur la même cible que l'AppHost. |
| OIDC `max_age` / `auth_time` | Les exemples demandent une authentification de moins de cinq minutes. Les parcours navigateur privés, secrets et publics passent par le handler du framework ; aucun switch ne désactive la validation .NET 11 du `auth_time` signé. |
| Microsoft.Extensions dans le framework partagé | Les dépendances directes Http et Options.ConfigurationExtensions sont alignées par TFM. Aucune référence directe aux neuf nouvelles bibliothèques partagées n'est ajoutée. |
| Configuration chaînée et valeurs vides | Les applications qui superposent des sources avec `AddConfiguration` doivent traiter une valeur vide comme une surcharge sous .NET 11. Nos projections conservent leurs noms ; n'utilise pas une chaîne vide comme substitut d'une configuration absente. |
| OpenTelemetry et Kestrel | Conservation des bibliothèques d'instrumentation et des validations HTTP/TLS ; aucun switch ne rétablit des comportements HTTP permissifs. |
| Suppression de DSA sur macOS | Les assertions utilisent RSA/RS256 ; aucun appel DSA dans le dépôt. |
| Compression, TAR et autres ruptures de bibliothèques | Aucun usage des API concernées dans les parcours de tokens ; les archives NuGet restent vérifiées par les scripts de packaging. |
| Matériel x86/x64 | .NET 11 demande x86-64-v2. Les minimums Arm64 restent inchangés ; il ne faut pas confondre minimum matériel et cible ReadyToRun. |

La [validation OIDC `max_age`](https://learn.microsoft.com/aspnet/core/breaking-changes/11/openid-connect-max-age-validates-auth-time), les [valeurs vides chaînées](https://learn.microsoft.com/dotnet/core/compatibility/extensions/11/chained-configuration-empty-string) et les [minimums matériels](https://learn.microsoft.com/dotnet/core/compatibility/jit/11/minimum-hardware-requirements) sont détaillés dans la documentation Microsoft.

Sur un poste où `dotnet --version` affiche 11 mais le build mentionne un ancien SDK, vérifie `MSBuildSDKsPath`. Une valeur héritée peut forcer les targets d'un ancien SDK. Corrige cette surcharge locale ; ne désactive pas le contrôle de framework du SDK.

## Migration depuis 10.0.0

Avec les packages stables `11.0.0`, garde `net10.0` pour rester sur .NET 10 ou choisis `net11.0` avec le SDK correspondant. Aligne les packages IdentityServer. L'API publique, les scopes, les sections projetées et le contrat 3 restent identiques ; aucune migration de données n'est nécessaire pour ce fournisseur en mémoire.

La version publiée `11.1.0` passe au contrat 4. La version publiée `11.2.0` relève le plancher à Aspire 17.0.0. La version `11.3.0` passe au contrat 5 : mets Hosting et Runtime à jour ensemble. Mets à jour les dépendances Aspire de l'AppHost et des tests distribués ensemble. Voir le [guide d'installation](demarrage.md#installer-dans-une-autre-solution).
