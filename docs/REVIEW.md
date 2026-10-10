# Revue Du Code

## Risques Restants Prioritaires

1. **Élevé : pas encore de permissions applicatives par utilisateur.** Le proxy protège l'accès public, mais chaque utilisateur autorisé est un opérateur complet. Ne pas donner cet accès à des amis avant d'intégrer sessions/OIDC et rôles. Voir [Program.cs](../backend/src/MediaHub.Api/Program.cs) et [RequestsController.cs](../backend/src/MediaHub.Api/Controllers/RequestsController.cs).
2. **Élevé : un seul worker doit tourner.** La reprise au démarrage remet tous les jobs Running en file ; une seconde instance pourrait reprendre des jobs encore actifs. L'exclusivité des encodages est locale au processus. Avant de supporter plusieurs workers, ajouter leases, propriétaire, expiration et verrou distribué. Voir [PipelineWorker.cs](../backend/src/MediaHub.Worker/PipelineWorker.cs).
3. **Élevé : migration des chemins persistés.** Les associations et payloads Windows ne sont pas utilisables tels quels sur Linux. Finir les jobs et réécrire les chemins avant un scan. Voir le guide de déploiement.
4. **Élevé : identité des requêtes d'amis incomplète.** Une requête sans utilisateur utilise Guid.Empty, incompatible avec une identité métier valide. Cette fonctionnalité doit être reliée à l'identité authentifiée, pas à un identifiant libre fourni par le navigateur. Voir [RequestsController.cs](../backend/src/MediaHub.Api/Controllers/RequestsController.cs).
5. **Moyen : identification concurrente des torrents.** Pour une URL .torrent, le client identifie un hash apparu depuis le snapshot précédent ; deux ajouts concurrents peuvent être confondus. Prévoir un tag/catégorie de corrélation par job et une vérification du contenu avant encodage. Voir [QBittorrentClient.cs](../backend/src/MediaHub.Infrastructure/Clients/QBittorrentClient.cs).
6. **Moyen : effets externes non transactionnels.** PostgreSQL, qBittorrent et MEGA ne partagent pas de transaction. Les reprises sont utiles mais ne garantissent pas une exécution exactement une fois. Ajouter des clés d'idempotence et une outbox avant les approbations mobiles. Tuer un client MEGAcmd ne garantit pas l'annulation d'un transfert géré par son serveur.
7. **Moyen : intégrité des références cloud.** CloudFile porte des identifiants sans relations EF explicites. Des scripts ou suppressions historiques ont déjà laissé des références orphelines. Prévoir une migration de contraintes après audit des données existantes. Voir [AppDbContext.cs](../backend/src/MediaHub.Infrastructure/Data/AppDbContext.cs).
8. **Moyen : montée en charge.** La liste de bibliothèque charge saisons/épisodes et chemins cloud de l'ensemble des médias, puis le frontend filtre en mémoire. Suffisant pour l'usage actuel, mais pagination, projections agrégées et recherche serveur seront nécessaires sur une grande bibliothèque. Voir [MediaController.cs](../backend/src/MediaHub.Api/Controllers/MediaController.cs).

Ces points ne sont pas présentés comme corrigés par cette livraison. Les permissions applicatives et la reprise distribuée nécessitent une évolution du modèle, pas un nettoyage cosmétique.

## Corrections Réalisées

- Initialisation de la base configurable en production, indépendante du mode Development.
- Remplacement du sémaphore nommé Windows par SemaphoreSlim portable.
- Fichiers encodés nommés par job, avec nettoyage des segments de chemin.
- Conservation des téléchargements jusqu'à l'upload persisté, et nettoyage limité au dossier de téléchargement configuré.
- Arrêt et observation des tâches du worker ; jobs interrompus reprogrammés plutôt que marqués en échec par simple arrêt.
- Arrêt des processus CLI MEGA lorsqu'une commande est annulée.
- Validation du contenu `Ok.` de la réponse qBittorrent, pas seulement du HTTP 200.
- Contrat de payload partagé : les règles de numérotation ne disparaissent plus lors d'une sélection des pistes.
- Politique de sélection des pistes mutualisée entre API et worker.
- Regex de langue inutilisée remplacée par une détection précise du marqueur MULTi.
- Protection du stockage par sentinelle, pour ne pas effacer des associations lorsque le montage est indisponible.
- URL d'API relative et proxy de développement ; frontend compilé servi par Nginx.
- Dockerfiles backend mutualisés, outils HandBrake/ffprobe/MEGA présents, secrets/configurations locales exclus du contexte.
- Deux presets exportés avec des noms distincts, sans modifier les paramètres de qualité.
- Proxy public HTTPS authentifié, interfaces internes liées à localhost, logs bornés et volumes persistants.
- Refus des écritures cross-origin au niveau du proxy public et réduction des logs HTTP portant des clés dans leurs URL.
- Mises à jour de sécurité compatibles des dépendances frontend ; l'audit npm fait désormais partie de la CI.
- Sondes de services et écran Système, lanceurs Windows/Linux, tests de régression et CI.
- Documentation historique corrigée : le suivi de fin de téléchargement et le nommage TMDB existaient déjà.

Les scripts Python et les presets originaux du workspace parent ne sont pas supprimés : ils restent des outils historiques indépendants. Le dépôt déployable contient désormais les ressources nécessaires.

## Vérification

Les tests automatisés couvrent login qBittorrent, sérialisation du payload, MULTi, sentinelle de montage et politique des pistes.
Validation effectuée sur Windows et le moteur Docker Linux local :

- 9 tests de régression réussis ; backend et frontend compilés.
- Aucun avis de vulnérabilité détecté par npm audit ni par l'audit NuGet direct/transitif à la date de livraison.
- Images Linux amd64 construites ; cinq services sains dans une stack isolée sur base neuve, migrations et proxy API fonctionnels.
- HandBrake Linux importe les presets Video et Animation.
- Proxy HTTPS de test : accès anonyme 401, accès authentifié 200, écritures cross-origin et sans Origin 403, écriture same-origin transmise à l'API.
- API, frontend et worker locaux redémarrés ; les six sondes répondent Disponible et le job interrompu a terminé.
- Écran Système contrôlé sur desktop et géométrie mobile 390 px sans débordement ni navigation superposée.

Les téléchargements/encodages/uploads réels, les services réseau du serveur, son DNS et les certificats publics nécessitent encore un test de bout en bout sur la cible.
Le heartbeat prouve que la boucle du worker vit ; il ne valide pas à lui seul HandBrake ni la session MEGA du worker.