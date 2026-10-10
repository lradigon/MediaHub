namespace MediaHub.Core.Releases;

/// <summary>
/// Une release candidate renvoyée par l'indexeur (Torznab / C411), enrichie
/// des attributs extraits de son nom.
/// </summary>
public class ReleaseCandidate
{
    /// <summary>Titre brut de la release (ex: "One Piece S21 MULTI WEBDL 1080p x264-Chris44").</summary>
    public required string Title { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoUrl { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public DateTimeOffset? PublishDate { get; init; }

    /// <summary>Nombre de fois où la release a été téléchargée (popularité, via Torznab "grabs").</summary>
    public int Grabs { get; init; }
    /// <summary>Freeleech : ne compte pas dans le ratio (downloadvolumefactor = 0).</summary>
    public bool IsFreeleech { get; init; }

    // Attributs extraits par le ReleaseParser
    public string? Resolution { get; set; }
    public string? VideoCodec { get; set; }
    public string? Source { get; set; }
    public bool IsMulti { get; set; }
    public string? ReleaseGroup { get; set; }

    /// <summary>Codec audio détecté (DTS, AC3, EAC3, AAC, FLAC, TrueHD…).</summary>
    public string? AudioCodec { get; set; }
    /// <summary>Canaux audio (ex: "5.1", "2.0", "7.1").</summary>
    public string? AudioChannels { get; set; }
    /// <summary>Plage dynamique : HDR, HDR10, HDR10+, DV (Dolby Vision).</summary>
    public string? DynamicRange { get; set; }
    /// <summary>Profondeur de couleur en bits (8, 10, 12).</summary>
    public int? BitDepth { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    /// <summary>Pack couvrant toute la série (intégrale) ou une plage de saisons.</summary>
    public bool IsCompletePack { get; set; }
    /// <summary>Dernière saison couverte pour un pack multi-saisons (ex: S01-S05 → 5).</summary>
    public int? SeasonEnd { get; set; }
}

/// <summary>Résultat de l'évaluation d'une release contre un profil.</summary>
public class ReleaseEvaluation
{
    public required ReleaseCandidate Release { get; init; }
    public bool Accepted { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();
}
