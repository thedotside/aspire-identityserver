# 0002 — Publication d'un AppHost avec un fournisseur externe

Statut : proposé, 18 novembre 2026. Décision à prendre par l'équipe ; rien n'est implémenté.

## Contexte

`AddIdentityServer()` refuse le mode publication : le fournisseur local ne doit jamais être déployé. Pour garder un AppHost publiable, la documentation demande de déclarer le fournisseur **et toutes ses projections** dans `if (builder.ExecutionContext.IsRunMode)`. En publication, chaque consommateur doit alors recevoir l'authority, l'identifiant client, les scopes et les credentials du fournisseur réel par ses propres paramètres, déclarés à la main, avec les mêmes noms de sections que les projections : c'est le contrat que décrit [Kubernetes](../kubernetes.md).

La configuration est donc écrite deux fois, une fois par mode, et rien ne vérifie que les deux versions restent alignées. Aspire propose pour d'autres intégrations une ressource qui s'exécute localement et se publie comme une référence externe (émulateurs Azure, chaînes de connexion).

## Contraintes

1. Le fournisseur local ne doit jamais apparaître dans un artefact de publication (manifeste, Compose, Kubernetes, Azure).
2. Aucun secret ni aucune clé **générés** pour le développement ne doivent être publiés. Un paramètre Aspire doté d'une valeur par défaut peut voir cette valeur écrite dans le manifeste : en publication, les credentials doivent être des paramètres secrets **sans** valeur par défaut, fournis au déploiement.
3. Les projections gardent leur API et leurs noms de sections ; seule la provenance des valeurs change.
4. Le motif `IsRunMode` actuel continue de fonctionner, sans changement.
5. Les règles d'enregistrement locales (utilisateurs de test, redirect URIs, échange de jetons) ne décrivent pas le fournisseur réel : elles n'ont pas d'effet en publication.

## Options

### A — Statu quo documenté

Garder `IsRunMode` et fournir un exemple complet de paramètres pour la publication. Aucun risque nouveau, mais la duplication reste et les erreurs d'alignement aussi.

### B — Fournisseur externe en publication, sur option (recommandée)

```csharp
var idp = builder.AddIdentityServer("idp", options => options.PublishAsExternalProvider = true);
var orders = idp.AddApiResource("orders-api").WithScopes("orders.read");
var worker = idp.AddClientCredentialsClient("worker")
    .WithGeneratedPrivateKeyJwt()
    .WithScopes("orders.read");
api.WithIdentityApiResource(orders, "Authentication:Inbound")
   .WithIdentityClient(worker, "Integrations:Orders:OAuth");
```

- **Exécution** : inchangée, fournisseur Duende local.
- **Publication** : aucune ressource exécutable n'est publiée. L'AppHost déclare le paramètre `idp-authority` (non secret, sans défaut). Les projections écrivent :
  - `Authority` : référence au paramètre `idp-authority` ;
  - `ClientId`, `Scopes`, `AuthenticationMethod`, `Audience` : les constantes déclarées, que le fournisseur réel doit reprendre ;
  - credentials : paramètres secrets **sans défaut** `idp-worker-secret`, ou `idp-worker-key` et `idp-worker-key-id`, quel que soit le mode de génération local. Les valeurs générées ne sortent jamais de l'exécution locale.
- Sans l'option, le refus actuel est conservé.

Coût : une branche « publication » dans `AddIdentityServer()`, les méthodes de credentials et les projections ; une propriété interne de provenance de l'authority (la propriété publique `Authority`, un `EndpointReference`, resterait réservée au mode exécution).

### C — Ressource distincte `AddExternalIdentityProvider()`

Un second type de ressource, sélectionné par l'appelant selon le mode. C'est plus explicite, mais les déclarations de clients et d'API doivent alors partager une abstraction commune aux deux types, ce qui élargit nettement l'API publique.

## Décision proposée

Option B, sur option explicite, pour ne rien changer aux AppHosts existants ni à la sécurité par défaut.

## Vérifications attendues avant implémentation

- [ ] Un AppHost avec l'option produit un manifeste sans ressource du fournisseur, avec `idp-authority` et des credentials secrets sans valeur par défaut (test de snapshot du manifeste).
- [ ] Aucune valeur générée (secret, clé, mot de passe, clé de signature) n'apparaît dans les artefacts de publication.
- [ ] Les projections publiées référencent les paramètres sous les mêmes clés de configuration qu'en exécution.
- [ ] Les publicateurs Compose et Kubernetes d'Aspire acceptent ce modèle ; le résultat est comparé au contrat de [Kubernetes](../kubernetes.md).
- [ ] Sans l'option, `aspire publish` échoue comme aujourd'hui.

## Questions ouvertes pour l'équipe

1. Le nom de l'option, et s'il faut aussi permettre un identifiant client différent en publication (paramètre `idp-worker-client-id`).
2. La place des scopes et audiences : constantes partagées, ou paramètres quand le fournisseur réel les nomme autrement.
3. Le niveau de garantie attendu par publicateur (manifeste, Compose, Kubernetes, Azure Container Apps).
