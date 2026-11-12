# Documentation Aspire IdentityServer

Cette documentation décrit le paramétrage de la version `11.1.0`, sur .NET 10 et .NET 11 et Aspire 13.6.5 ou ultérieur.

Il s'agit de la prochaine version mineure destinée aux fonctionnalités ; `11.0.x` est réservé aux correctifs. La dernière version stable est [11.0.0](https://github.com/thedotside/aspire-identityserver/releases/tag/11.0.0), avec des assets explicites pour chaque framework. Consulte la [compatibilité .NET 10 / .NET 11](compatibilite-dotnet.md), y compris les limites du support annoncé par Duende.

L'intégration permet de simuler plusieurs fournisseurs OAuth/OIDC indépendants dans un AppHost de développement ou de tests. Duende IdentityServer exécute les protocoles dans des processus .NET locaux.

## Guides

| Page | Contenu |
| --- | --- |
| [Démarrage](demarrage.md) | Prérequis, installation locale des packages, premier AppHost et lancement des exemples. |
| [Compatibilité .NET 10 / .NET 11](compatibilite-dotnet.md) | Cibles des packages, SDK, runtime du fournisseur et audit de migration. |
| [Fournisseurs et ressources API](fournisseurs.md) | IDP, utilisateurs, scopes, audiences, isolation et configuration du serveur. |
| [Clients et authentification](clients.md) | Authorization code, client credentials, PKCE, secrets, clés privées et redirections. |
| [JWK privée Base64](jwk-base64.md) | Depuis `0.1.0-alpha.2` : génération/import RSA, configurations BFF et CC Xp/Capa, migration depuis PEM et encodage Kubernetes. |
| [Token Exchange](token-exchange.md) | Depuis `0.1.0-alpha.2` : échange utilisateur → client machine sur le même IdP et suivi du scénario à deux IdP. |
| [Projections de configuration](projections.md) | Champs injectés, exemples JSON, variables d'environnement, mapping personnalisé et plusieurs composants. |
| [Intégration des consommateurs](integration.md) | Lecture des options, validation JWT dans une API, handler OIDC, BFF, React et demandes de tokens. |
| [Kubernetes et déploiement](kubernetes.md) | Contrat de configuration d'un consommateur, ConfigMap, Secret, YAML et limites de publication. |
| [Référence de l'API](reference.md) | Signatures, valeurs par défaut, propriétés, validations et paramètres non exposés. |
| [Développement et dépannage](developpement.md) | Tests, packages, CI, erreurs courantes et préparation d'une publication. |
| [Publication NuGet](publication.md) | Tags SemVer, publication automatique, identité de l'organisation et réservation du préfixe. |

## Parcours conseillés

- Pour démarrer : [installation](demarrage.md), puis [fournisseurs](fournisseurs.md) et [clients](clients.md).
- Pour brancher une application existante : [projections](projections.md), puis [intégration](integration.md).
- Pour comprendre la correspondance avec Kubernetes : [projections](projections.md), puis [Kubernetes](kubernetes.md).
- Pour contribuer ou publier un package : [référence](reference.md), puis [développement](developpement.md).

Les exemples partiels indiquent les variables ou ressources déjà déclarées qu'ils supposent. Les valeurs entre `<...>` sont des marqueurs de documentation. Les exemples Kubernetes utilisent des adresses fictives et ne constituent pas un déploiement de l'IDP fourni par ce projet.

## Périmètre de cette version

| Fonction | État |
| --- | --- |
| Plusieurs IDP indépendants, HTTPS, utilisateurs de test | Implémenté. |
| Authorization code, PKCE, client credentials | Implémenté. |
| Secrets et assertions RSA/RS256 `private_key_jwt` | Implémenté. |
| Scopes, ressources API, audiences et projections | Implémenté. |
| Helper d'assertion et exemple ASP.NET Core OIDC | Implémenté. |
| Token Exchange local, sélection des informations utilisateur | Disponible depuis `0.1.0-alpha.2`. Voir le [guide](token-exchange.md). |
| Claims de profil des utilisateurs de test, scopes d'identité personnalisés, refresh tokens (`offline_access`), Token Exchange par délégation | Disponible depuis `0.1.0-alpha.4`. Voir [les fournisseurs](fournisseurs.md) et [le guide](token-exchange.md#scopes-choisis-par-le-client-machine). |
| Token Exchange entre deux IdP | [Phase 2 à implémenter](roadmap/token-exchange-cross-idp.md) après validation du parcours local. |
| Adaptateur automatique Duende BFF | Non fourni. Le BFF configure sa bibliothèque avec les valeurs projetées. |
| Utilisateurs persistants, fédération | Hors périmètre de cette intégration. |
| Déploiement du fournisseur sur Kubernetes ou en production | Non pris en charge ; publication Aspire refusée. |
| Benchmark comparatif face à Keycloak | Non réalisé. |

La validation locale consignée le 31 octobre 2026, sur macOS arm64, comprend 162 tests réussis et deux consommateurs indépendants des packages. Elle ne prouve pas la validation sur d'autres systèmes ni le résultat actuel de GitHub Actions. Voir [les vérifications](developpement.md).

[Retour au README du projet](../README.md)
