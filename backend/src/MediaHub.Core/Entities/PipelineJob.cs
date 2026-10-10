namespace MediaHub.Core.Entities;

/// <summary>
/// Une étape de traitement asynchrone (recherche, download, encode, upload, notify).
/// Le Worker consomme cette table comme une file de tâches.
/// </summary>
public class PipelineJob
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public JobKind Kind { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;

    public Guid? MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Épisode ciblé (pour les séries). Null pour un film.</summary>
    public Guid? EpisodeId { get; set; }

    /// <summary>Progression 0-100, avec deux décimales lorsque la source le permet.</summary>
    public double Progress { get; set; }

    /// <summary>Nombre de sources (seeds) observé pendant le téléchargement.</summary>
    public int Seeders { get; set; }

    /// <summary>Taille totale du contenu en octets (connue pendant le download).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Vitesse de téléchargement instantanée en octets/seconde.</summary>
    public long DownloadSpeed { get; set; }

    /// <summary>Vitesse de conversion HandBrake en images par seconde.</summary>
    public double? EncodingFps { get; set; }

    /// <summary>Charge utile JSON spécifique à l'étape (URL de la release, chemins, etc.).</summary>
    public string? PayloadJson { get; set; }

    public string? Error { get; set; }
    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public record ReleasePayload(
    string? DownloadUrl,
    string? Title,
    string? ContentPath,
    string? OutputPath,
    string? TorrentHash,
    IReadOnlyList<int>? AudioTrackIndexes = null,
    IReadOnlyList<int>? SubtitleTrackIndexes = null,
    int? ReleaseSeasonNumber = null,
    int? TmdbSeasonNumber = null,
    int? FirstTmdbEpisodeNumber = null);
