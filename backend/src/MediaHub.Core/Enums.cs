namespace MediaHub.Core;

/// <summary>Type de média géré par la bibliothèque.</summary>
public enum MediaType
{
    Movie = 0,
    Series = 1,
    Anime = 2
}

/// <summary>État de suivi d'un média (film ou série) dans la bibliothèque.</summary>
public enum MediaStatus
{
    /// <summary>Suivi mais rien de téléchargé.</summary>
    Wanted = 0,
    /// <summary>Au moins une release en cours dans la pipeline.</summary>
    Processing = 1,
    /// <summary>Disponible sur le cloud et lisible dans Plex.</summary>
    Available = 2,
    /// <summary>Ignoré (ne plus chercher de release).</summary>
    Ignored = 3
}

/// <summary>État d'un épisode de série.</summary>
public enum EpisodeStatus
{
    /// <summary>Diffusé/attendu mais pas encore trouvé.</summary>
    Missing = 0,
    /// <summary>Release trouvée, dans la pipeline.</summary>
    Processing = 1,
    /// <summary>Disponible sur le cloud.</summary>
    Available = 2,
    /// <summary>Pas encore diffusé.</summary>
    Unaired = 3
}

/// <summary>Rôle utilisateur pour la partie "requêtes d'amis".</summary>
public enum UserRole
{
    Friend = 0,
    Admin = 1
}

/// <summary>État d'une requête d'ajout faite par un ami.</summary>
public enum RequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>Étape de la pipeline de traitement d'une release.</summary>
public enum JobKind
{
    /// <summary>Recherche d'une release valide via l'indexeur.</summary>
    Search = 0,
    /// <summary>Téléchargement via le client torrent.</summary>
    Download = 1,
    /// <summary>Ré-encodage HandBrake avec le preset.</summary>
    Encode = 2,
    /// <summary>Upload vers le stockage cloud (MEGA).</summary>
    Upload = 3,
    /// <summary>Notification de disponibilité (rafraîchit Plex, etc.).</summary>
    Notify = 4,
    /// <summary>Scan du stockage existant pour peupler la bibliothèque.</summary>
    Scan = 5,
    /// <summary>Actualise les métadonnées de la bibliothèque depuis TMDB.</summary>
    MetadataRefresh = 6,
    /// <summary>Vérifie ou attend le choix des pistes audio et sous-titres avant encodage.</summary>
    TrackSelection = 7
}

/// <summary>État d'un job de pipeline.</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    WaitingForSelection = 5
}
