# MediaHub

## Guides

- [Déploiement Linux, exploitation et GitHub](docs/DEPLOYMENT.md)
- [Revue du code, corrections et risques restants](docs/REVIEW.md)
- [Watchers, approbations mobiles et alertes](docs/ROADMAP.md)

L'écran **Système** vérifie PostgreSQL, TMDB, C411, qBittorrent, le stockage et le heartbeat du worker.
Le déploiement Docker inclut maintenant le frontend, les outils média et un proxy HTTPS authentifié optionnel.

Remplacement custom de la stack Jellyseerr + Sonarr + Radarr + Prowlarr, réduit à
**un back (.NET 10)** et **un front (React/Vite)**. Objectifs :

- une bibliothèque unifiée films / séries / animés ;
- l'inventaire de ce qui est réellement sur le cloud (MEGA) ;
- des requêtes d'amis à valider (comme Jellyseerr) ;
- une pipeline automatique **recherche → téléchargement → ré-encodage HandBrake → upload MEGA → notification Plex**.

## Architecture

```
Front (React/Vite)  ──►  API (.NET)  ──►  PostgreSQL
                             │
                             ├─► TMDB (métadonnées)
                             ├─► C411 via Torznab (indexeur)
                             └─► qBittorrent (API Web)

Worker (.NET)  ──► lit la file PipelineJobs ──► qBittorrent → HandBrakeCLI → MEGAcmd → Plex
```

### Projets backend (`backend/`)

| Projet | Rôle |
| --- | --- |
| `MediaHub.Core` | Domaine (entités, enums), **parsing/scoring des releases** (`ReleaseParser`), interfaces. |
| `MediaHub.Infrastructure` | EF Core + PostgreSQL, clients externes (TMDB, Torznab, qBittorrent, MEGAcmd, HandBrakeCLI), DI. |
| `MediaHub.Api` | API REST (bibliothèque, recherche, requêtes, jobs). |
| `MediaHub.Worker` | Orchestrateur de la pipeline (consomme `PipelineJobs`). |

## Pré-requis

- .NET SDK 10, Node.js 22+
- PostgreSQL (ou le service `db` de Compose, après préparation de `.env`)
- Sur l'hôte du Worker : `HandBrakeCLI`, `ffprobe` et `MEGAcmd` authentifié

## Configuration (secrets)

Ne jamais committer les clés. En local, utiliser les *user-secrets* .NET ou des
variables d'environnement. Sections attendues (`appsettings.json`) :

- `Tmdb:ApiKey` — jeton Bearer TMDB v4
- `Torznab:BaseUrl` / `Torznab:ApiKey` — C411
- `QBittorrent:BaseUrl` / `Username` / `Password`
- `Mega:MegaCmdDir` / `RemoteRoot`
- `HandBrake:CliPath` / `PresetFilePath` / `MoviePreset` / `AnimePreset`

```bash
cd backend/src/MediaHub.Api
dotnet user-secrets init
dotnet user-secrets set "Tmdb:ApiKey" "<token>"
dotnet user-secrets set "Torznab:ApiKey" "<c411-key>"
dotnet user-secrets set "QBittorrent:Password" "<pwd>"
```

## Lancer en développement

Préparer `.env` depuis `.env.example` avant toute commande Compose, avec des chemins de stockage adaptés au poste. Compose valide cette configuration même si seul `db` est demandé.
Lancer chaque service dans son propre terminal depuis le dossier indiqué. API et worker ont des configurations locales distinctes ; ne pas publier leurs réglages Development.

```bash
# 1. Base de données
docker compose up -d db

# 2. API (applique les migrations + sème un profil par défaut au démarrage)
cd backend
dotnet run --project src/MediaHub.Api        # http://localhost:5080

# 3. Worker (depuis la racine mediahub, dans un autre terminal)
cd backend/src/MediaHub.Worker
dotnet run

# 4. Front (depuis la racine mediahub, dans un autre terminal)
cd frontend
npm ci
npm run dev                                   # http://localhost:5173
```

## Migrations EF Core

```bash
cd backend
dotnet ef migrations add <Nom> -p src/MediaHub.Infrastructure -s src/MediaHub.Api -o Data/Migrations
dotnet ef database update -p src/MediaHub.Infrastructure -s src/MediaHub.Api
```

## Profil de qualité par défaut

`ReleaseProfile` (semé au 1er démarrage) : **1080p, MULTi requis, x264/h264 autorisés,
x265/HEVC rejetés, sources WEB-DL/BluRay/Remux**. Modifiable en base ; le parsing/scoring
vit dans `MediaHub.Core/Releases/ReleaseParser.cs`.

## État & TODO

Squelette fonctionnel. À durcir avant prod :

- **Auth** (rôles Admin/Ami) — indispensable avant d'exposer aux amis.
- **Watchers de nouvelles releases et approbations mobiles** : voir la feuille de route. Le suivi de fin de téléchargement et le nommage TMDB sont déjà implémentés.
- **Scan Plex ciblé** dans l'étape `Notify`.
- **Inventaire cloud** (`CloudFile`) : job de synchronisation périodique via `mega-find`.
- Parsing des releases à enrichir selon les conventions réelles de C411.
