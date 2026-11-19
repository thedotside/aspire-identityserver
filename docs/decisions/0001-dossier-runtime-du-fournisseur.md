# 0001 — Dossier runtime du fournisseur

Statut : accepté. Proposé le 31 octobre 2026, livré dans `0.1.0-alpha.3`, stable depuis `10.0.0`.

## Contexte

Le paquet `TheDotSide.Aspire.IdentityServer.Runtime` ne contient que nos assemblies : Duende et les autres dépendances restent des dépendances NuGet ordinaires, restaurées sur le poste du consommateur avec leurs licences. Le fournisseur tourne pourtant dans son propre processus, lancé par l'AppHost depuis un dossier `identityserver/` construit au build du consommateur.

Jusqu'à `0.1.0-alpha.2`, la cible `buildTransitive` copiait dans ce dossier **toutes** les dépendances du consommateur, avec son `deps.json` : 116 fichiers et 36 Mo pour l'AppHost d'exemple, 223 fichiers et 42 Mo pour le projet de tests, Playwright et le framework de test compris. Le `runtimeconfig.json` était figé sur .NET `10.0.0` sans `rollForward` : un AppHost `net11.0` sur un poste sans runtime 10 ne pouvait pas lancer le fournisseur.

## Décision

- Au build, la cible lit le graphe de paquets restauré par le consommateur, avec la tâche SDK `ResolvePackageDependencies`. Elle calcule la fermeture de `TheDotSide.Aspire.IdentityServer.Runtime` et ne copie que ses assemblies, dans les versions résolues pour ce consommateur. À l'intérieur du dépôt, l'assembly du serveur vient de la référence de projet.
- Le dossier n'a pas de `deps.json` : l'hôte .NET charge alors les assemblies qu'il contient. Une dépendance avec des actifs propres à un RID (`runtimes/...`) ne pourrait pas être chargée ainsi : le build échoue avec un message explicite.
- Le dossier appartient à l'intégration. Les assemblies et le `deps.json` qui n'appartiennent plus à la fermeture y sont supprimés, pour qu'aucune assembly périmée ne soit chargée ; les autres fichiers restent. Ses fichiers sont déclarés dans `FileWrites` : `dotnet clean` et les builds incrémentaux les gèrent comme les autres sorties.
- Le `runtimeconfig.json` vise `net10.0` avec `rollForward: Major` : le fournisseur tourne sur .NET 10 quand il est installé, sinon sur la version majeure suivante.
- Hosting et Runtime partagent un contrat de configuration versionné (`IdentityServer__ContractVersion`), vérifié au démarrage du fournisseur.

## Conséquences

- Le dossier de l'AppHost d'exemple passe à 18 fichiers et 4 Mo au moment de cette décision, identique pour les projets de test. Aucune assembly de l'AppHost, d'Aspire, de Playwright ou du framework de test n'entre dans le processus du fournisseur.
- Le fournisseur utilise exactement les versions que le consommateur a restaurées : un seul graphe NuGet, aucune copie de Duende dans nos archives.
- La cible dépend d'une tâche du SDK conservée pour la compatibilité (`ResolvePackageDependencies`, appelée par le SDK seulement avec `EmitLegacyAssetsFileItems`) et d'une tâche inline `RoslynCodeTaskFactory`. Son retrait d'un futur SDK casserait le build des consommateurs : la CI, qui suit le SDK par Renovate, le verrait avant une publication, et le workflow hebdomadaire `compatibility` construit l'AppHost d'exemple avec la préversion du SDK suivant dès qu'elle paraît. Le même workflow construit la solution avec le MSBuild de Visual Studio (.NET Framework) ; tant qu'aucune de ses exécutions n'a réussi, ces deux points restent non vérifiés.
- Une future dépendance à actifs natifs ou propres à un RID exigera de générer un `deps.json` limité à la fermeture.
- Les tests unitaires et le smoke test vérifient l'absence de `deps.json` et d'assemblies du consommateur dans le dossier.

## Alternatives écartées

- Livrer le serveur publié, avec son propre `deps.json` : il faudrait embarquer les binaires Duende dans nos archives, ce que la licence et la règle du dépôt excluent.
- Lancer le serveur avec son `deps.json` de build et le cache NuGet comme dossier de recherche : les versions exactes du build peuvent manquer dans le cache du consommateur, qui a pu résoudre des versions supérieures.
- Livrer les sources du serveur et le lancer avec `AddCSharpApp`, Duende restant restauré depuis NuGet : supprimerait la copie, au prix d'une compilation au premier lancement et d'un dossier de build à gérer. Piste à réévaluer si un `deps.json` devient nécessaire.
