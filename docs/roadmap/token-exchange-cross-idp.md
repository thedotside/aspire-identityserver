# Phase 2 : Token Exchange entre deux IdP

Statut : implémenté pour `11.1.0`, en relecture. Le guide [Token Exchange](../token-exchange.md#entre-deux-idp) décrit le fonctionnement ; cette page garde les décisions et l'état des critères.

Suivi GitHub : [issue #4](https://github.com/thedotside/aspire-identityserver/issues/4).

## Parcours attendu

1. Le navigateur termine un Authorization Code + PKCE auprès de l'IdP2.
2. Un client machine confidentiel de l'IdP1 présente l'access token IdP2 et ses propres credentials au token endpoint de l'IdP1.
3. L'IdP1 valide la confiance explicitement déclarée et émet un jeton local, accepté uniquement par les API autorisées de l'IdP1.

La configuration doit rester aussi simple que le parcours local : des références d'objets Aspire et une déclaration de confiance explicite. Aucun validateur Duende ni configuration manuelle de JWKS ne devrait être requis dans l'AppHost. L'API exacte sera arrêtée après retour sur [la phase 1](../token-exchange.md).

## Décisions nécessaires

- Déclarer une confiance directionnelle IdP1 ← IdP2, inactive par défaut. IdP2 ne fait pas confiance à IdP1 par transitivité.
- Conserver les références différées d'issuer et les credentials secrets Aspire.
- Valider issuer exact, signature, algorithme et type autorisés, expiration, audience source et client utilisateur source. Le client machine s'authentifie uniquement auprès d'IdP1.
- Découvrir les clés exclusivement à partir du fournisseur configuré. Définir le cache et le renouvellement JWKS ; refuser une clé inconnue après rafraîchissement contrôlé.
- Définir le mapping entre les scopes/audiences IdP2 et IdP1 sans élargissement implicite des droits.
- Définir une identité locale stable et sans collision à partir du couple `(issuer source, sub source)`. Deux utilisateurs `alice` de fournisseurs différents ne doivent pas être confondus. Aucun rattachement automatique par nom ou e-mail.
- Sélectionner les claims utilisateur transférés ; préserver la provenance et l'identité du client machine. Ne jamais recopier les claims de protocole ou les champs de preuve de possession.
- Limiter la validité du jeton local à celle du jeton source. Pas de refresh token ou d'échange en chaîne implicite.

## Critères d'acceptation

- [x] La phase 1 est fusionnée, sa CI est verte et son API AppHost est validée (stable depuis `10.0.0`).
- [x] Un seul AppHost réel héberge deux IdP indépendants et l'API IdP1 ; les tests utilisent des contextes navigateur isolés.
- [x] Un jeton utilisateur IdP2 est accepté par l'échange IdP1 seulement après déclaration explicite de confiance.
- [x] Le jeton échangé a un issuer et une signature IdP1, l'audience attendue, les scopes mappés autorisés et les claims sélectionnés.
- [x] L'API IdP1 accepte ce jeton et refuse directement le jeton IdP2.
- [x] Les mêmes identifiants de sujet sur les deux IdP ne provoquent aucune collision.
- [ ] Confiance inverse, IdP3 non déclaré, mauvais client source, mauvaise audience, signature/algorithme/type erronés, expiration et escalade de scopes sont refusés. Testés : confiance inverse, client de même nom d'un autre IdP, jeton machine, audience, escalade de scope, jeton modifié, clé jamais publiée. Couverts par la validation sans test dédié : algorithme et type autres que RS256/`at+jwt`, expiration, IdP3 (toute autre valeur d'issuer est refusée faute de politique).
- [ ] Le renouvellement des clés et une clé inconnue sont testés sans validation permissive ni TLS désactivé. Testé : clé inconnue refusée après rafraîchissement borné. Non testé : rotation effective d'une clé de l'IdP2.
- [x] La documentation couvre la déclaration AppHost, les projections, les requêtes et les limites.
- [x] Build, tests, vrais packages et consommateur isolé passent en local ; la phase 1 reste inchangée pour ses consommateurs.
