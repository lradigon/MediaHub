namespace MediaHub.Core.Entities;

/// <summary>
/// Un film ou une série suivi dans la bibliothèque.
/// Les épisodes/saisons ne sont peuplés que pour les séries et animés.
/// </summary>
public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identifiant TMDB (source des métadonnées).</summary>
    public int? TmdbId { get; set; }

    public MediaType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string? OriginalLanguage { get; set; }
    public string? Overview { get; set; }
    public int? Year { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }

    public MediaStatus Status { get; set; } = MediaStatus.Wanted;

    /// <summary>Chemin du dossier de ce média sur le stockage local/monté (rempli au scan).</summary>
    public string? StoragePath { get; set; }

    /// <summary>Profil de qualité/filtre de release appliqué à ce média.</summary>
    public Guid? ReleaseProfileId { get; set; }
    public ReleaseProfile? ReleaseProfile { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Season> Seasons { get; set; } = new List<Season>();
    public ICollection<EpisodeNumberingRule> EpisodeNumberingRules { get; set; } = new List<EpisodeNumberingRule>();
}
