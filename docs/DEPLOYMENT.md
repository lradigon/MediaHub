# Déploiement Et Exploitation

## Architecture

Internet → Caddy (HTTPS + authentification HTTP Basic) → frontend Nginx → API → PostgreSQL.
Le worker pilote qBittorrent, HandBrakeCLI et MEGAcmd. Un seul worker est supporté.

Les ports PostgreSQL, API, frontend direct et qBittorrent WebUI sont liés à `127.0.0.1`.
Seuls Caddy et le port pair-à-pair du client torrent sont publics.
Ne pas changer ces liaisons pour contourner l'authentification du proxy.
Cette authentification convient à un opérateur : tous les comptes du proxy ont les mêmes droits.
L'accès d'amis avec des permissions différentes nécessite encore une authentification applicative.

## Préparer Linux

Pré-requis : Docker Engine, Docker Compose v2 avec `--wait`, Bash, jq, un domaine et un stockage déjà monté.
Le Dockerfile supporte amd64 et arm64 ; vérifier les images sur l'architecture du serveur avant migration.
MEGAcmd est téléchargé dans sa version épinglée depuis le dépôt officiel et son checksum est vérifié.

1. Cloner le dépôt sur le serveur.
2. Créer `.env` depuis `.env.example`, avec des permissions `0600`.
3. Configurer les vrais chemins Linux de `LIBRARY_PATH` et `DOWNLOADS_PATH`.
4. Configurer les jetons TMDB/C411 et les identifiants qBittorrent. Ne jamais les mettre dans Git.
5. Choisir un mot de passe PostgreSQL long, aléatoire et alphanumérique.
6. Dans le stockage réellement monté, créer `.mediahub-mounted`. Le scan refuse de modifier les associations si cette sentinelle disparaît.
7. Les conteneurs .NET et qBittorrent utilisent UID/GID `1654`. Les téléchargements doivent être accessibles à cet utilisateur. Pour un montage FUSE, configurer l'accès à cet UID, par exemple `allow_other` si adapté.

Les presets exportés sont déjà inclus dans `presets/handbrake.json`. Les sources originales à la racine de l'ancien workspace et le script Python historique ne sont pas nécessaires pour déployer MediaHub.
Pour réexporter les presets depuis les deux fichiers historiques, utiliser `scripts/Export-Presets.ps1` avant de transférer le dépôt.

## Premier Démarrage

```bash
bash scripts/mediahub.sh up
```

Le site privé de préparation répond sur `http://localhost:8085`. Un tunnel SSH permet d'y accéder à distance.
Au premier démarrage de qBittorrent, lire son mot de passe temporaire directement dans ses logs sur le serveur, se connecter à sa WebUI via tunnel et définir un mot de passe permanent.
Mettre ce mot de passe dans `.env`, puis recréer API/worker pour charger la configuration.
L'état Docker de qBittorrent indique que sa WebUI répond ; l'écran Système vérifie également les identifiants utilisés par MediaHub.

Authentifier séparément les deux sessions MEGA, sans placer le mot de passe dans une ligne de commande ou dans `.env` :

```bash
docker compose exec api mega-cmd
docker compose --profile pipeline exec worker mega-cmd
```

Dans chaque shell interactif MEGA, lancer `login adresse@email` et saisir le mot de passe directement au prompt.
Les sessions de l'API et du worker ont des volumes distincts, afin de ne pas partager le cache d'un serveur MEGAcmd entre deux processus.
Le heartbeat du worker ne prouve pas que sa session MEGA est connectée ; vérifier celle-ci lors de la préparation.

## Activer Le Domaine Public

Configurer l'enregistrement DNS vers le serveur et autoriser les ports TCP 80/443. Ne pas exposer les interfaces internes.
Configurer `MEDIAHUB_DOMAIN`, `MEDIAHUB_AUTH_USER` et un hash bcrypt dans `.env`.

```bash
docker run --rm -it caddy:2-alpine caddy hash-password
```

Saisir le mot de passe au prompt. Enregistrer uniquement le hash dans `.env`, entre quotes simples pour conserver ses caractères `$`.

```bash
bash scripts/mediahub.sh public
```

Caddy gère les certificats TLS. Les lanceurs refusent les paramètres publics d'exemple.
Les écritures publiques doivent porter un en-tête `Origin` correspondant à `https://MEDIAHUB_DOMAIN`. Le navigateur le fournit pour les appels de l'interface ; un client API externe authentifié doit le fournir explicitement.
Si un autre reverse proxy utilise déjà 80/443, intégrer MediaHub à ce proxy plutôt que démarrer un second Caddy sur ces ports.
Le HTTPS réel, le DNS, le firewall et les permissions de montage restent à valider sur le serveur cible.

## Commandes Courantes

```bash
bash scripts/mediahub.sh status
bash scripts/mediahub.sh restart public
bash scripts/mediahub.sh rebuild public
bash scripts/mediahub.sh logs
bash scripts/mediahub.sh backup
bash scripts/mediahub.sh down
```

Sous Windows, utiliser `./scripts/mediahub.ps1 -Action status`, et `-Public` pour redémarrer ou reconstruire la stack publique.
`restart` recrée les conteneurs pour relire `.env`. `rebuild` reconstruit le code. `down` ne supprime pas les volumes.
Ne pas utiliser `docker compose down -v` pour une instance contenant des données.
Les logs tournent avec une taille et un nombre de fichiers bornés.
Le statut détaillé des dépendances est accessible dans l'écran Système ; `/health` vérifie le processus API et `/health/ready` la connexion PostgreSQL.

## Exporter Les Données Windows

Ne pas lancer deux workers contre la même base. Terminer ou annuler les traitements avant migration.
Sauvegarder PostgreSQL après arrêt du worker et conserver la sauvegarde hors de Git, avec une copie hors serveur.
Les téléchargements, fichiers encodés, sessions MEGA et paramètres qBittorrent ne sont pas transportés par le dépôt Git.

Sur une base Linux neuve, démarrer seulement `db`, restaurer la sauvegarde avec `pg_restore`, puis démarrer API/web.
Ne jamais utiliser `--clean` sur une base existante sans sauvegarde et validation explicite.
Les migrations sont appliquées par l'API avant que le worker puisse démarrer.

Avant tout scan sur Linux, réécrire les chemins Windows persistés dans `MediaItems.StoragePath`.
Exemple à adapter et tester dans une transaction pour l'ancien dossier racine :

```sql
BEGIN;
UPDATE "MediaItems"
SET "StoragePath" = regexp_replace(
    replace("StoragePath", chr(92), '/'),
    '^[A-Za-z]:/Films - Séries - Animés/', '/library/')
WHERE "StoragePath" ~ '^[A-Za-z]:';
SELECT "Title", "StoragePath" FROM "MediaItems" WHERE "StoragePath" IS NOT NULL;
-- COMMIT uniquement après vérification des chemins et du nombre de lignes.
ROLLBACK;
```

Les anciens payloads de jobs contiennent aussi des chemins Windows : ne pas les remettre en file sans avoir transféré les fichiers et adapté ces chemins.
Changer `POSTGRES_PASSWORD` dans `.env` ne change pas le mot de passe d'un volume PostgreSQL déjà initialisé : utiliser une rotation SQL coordonnée.

## GitHub Public

Le périmètre à publier est le dossier `mediahub`, pas les données du workspace parent.
Les logs, binaires, dépendances, réglages Windows locaux, `.env`, médias et sauvegardes sont exclus.
Le workflow GitHub Actions exécute les tests .NET, le build frontend et les builds Docker Linux.
Il ne déploie rien et ne publie aucune image automatiquement.

Le dépôt cible est **public** : `https://github.com/lradigon/MediaHub.git`. Vérifier les fichiers avant chaque commit, pas seulement au premier push.
Ne jamais publier avant d'avoir inspecté les fichiers et recherché d'éventuels secrets.
La publication nécessite une authentification directe de l'utilisateur ; aucune clé d'accès ne doit être envoyée dans la conversation. Utiliser l'adresse GitHub noreply pour éviter de publier une adresse email personnelle dans les commits.