# Veille Et Approbations Mobiles

## Choix Recommandé

Conserver API + worker + PostgreSQL. Ajouter des tâches planifiées dans le worker plutôt qu'un microservice supplémentaire.
Les observations des releases et les décisions humaines doivent être persistées avant toute notification.
Pour commencer, Telegram est pratique pour les boutons Accepter/Refuser ; garder une interface de fournisseur pour pouvoir remplacer Telegram par Discord.
Aucun fournisseur ni compte de messagerie n'est imposé par le code actuel.

## Modèle À Ajouter

- `MediaWatch` : média/épisodes suivis, profil, fréquence, activation et prochaine vérification.
- `ReleaseObservation` : identifiant stable/hash, couverture, source, première observation et dernière observation.
- `ReleaseApproval` : décision Pending/Accepted/Rejected/Expired, utilisateur autorisé et durée de validité.
- `NotificationOutbox` : messages à envoyer, retries, identifiant du fournisseur et date d'envoi.
- `ServiceIncident` : composant, état précédent/nouveau, début d'incident et rétablissement.

Ajouter des index uniques pour empêcher les notifications et pipelines en double.
Une release refusée ne doit pas être renotifiée à chaque scan ; une nouvelle release distincte peut l'être.

## Flux

1. La tâche planifiée prend un lease et vérifie uniquement les watchers arrivés à échéance.
2. Elle interroge C411 avec backoff, limites de débit et jitter, puis applique le profil existant.
3. Elle persiste l'observation, la demande d'approbation et le message d'outbox dans une seule transaction.
4. L'outbox envoie le message avec couverture, taille, qualité et boutons de décision.
5. L'approbation vérifie l'utilisateur, la signature/identité du fournisseur, l'expiration et l'état Pending.
6. Une transition atomique Pending → Accepted crée un seul job Download avec un identifiant de corrélation.
7. Un second clic ne crée rien. Une décision Refused/Expired ne lance jamais la pipeline.

Ne pas mettre un simple lien GET public qui démarre un téléchargement. Utiliser une action POST authentifiée ou une callback vérifiée, avec un identifiant opaque et une protection anti-rejeu.
Réévaluer la disponibilité et la pertinence d'une release devenue ancienne avant de lancer le téléchargement.

## Monitoring Et Alertes

Les sondes actuelles sont manuelles et ne constituent pas encore un système d'alerte.
Prochaine étape : checks périodiques avec état persisté, durée minimale d'incident et seuil de plusieurs échecs avant notification.
Notifier les transitions DOWN et RECOVERED, pas chaque échec de polling.
Différencier service injoignable, identifiants refusés, quota/429, montage indisponible, espace disque faible et heartbeat périmé.

Ajouter une sonde indépendante du serveur (par exemple Uptime Kuma) : un service arrêté ne peut pas envoyer lui-même une alerte fiable.
Le contrôle externe doit passer par HTTPS et l'authentification du proxy avec un compte dédié.
Pour des métriques plus détaillées, exporter ensuite durée des jobs, taille de file, taux d'échec et espace disque vers Prometheus/Grafana.
Ne pas transmettre de secrets, URL privées de téléchargement ou données de compte dans les alertes.

## Ordre De Livraison

1. Valider déploiement Linux, sauvegarde/restauration, permissions et authentification.
2. Corrélation/idempotence des jobs et modèle d'identité applicative.
3. Watchers et observations persistées, visibles dans l'interface sans lancement automatique.
4. Outbox + premier fournisseur de notification.
5. Approbations mobiles sécurisées et tests de double clic/rejeu.
6. Alertes périodiques et surveillance externe indépendante.