namespace MediaHub.Core.Entities;

/// <summary>
/// Règles de filtrage/scoring des releases. C'est l'équivalent des "Custom Formats"
/// et "Quality Profiles" des *arr, mais réduit à ce dont tu as besoin.
/// </summary>
public class ReleaseProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    /// <summary>Impose une piste multi-langue (MULTi). Rejette si absent.</summary>
    public bool RequireMulti { get; set; } = true;

    /// <summary>Résolutions autorisées (ex: "1080p"). Vide = toutes.</summary>
    public List<string> AllowedResolutions { get; set; } = new() { "1080p" };

    /// <summary>Codecs vidéo autorisés (ex: "x264", "h264").</summary>
    public List<string> AllowedVideoCodecs { get; set; } = new() { "x264", "h264" };

    /// <summary>Codecs vidéo rejetés (ex: "x265", "h265", "hevc").</summary>
    public List<string> RejectedVideoCodecs { get; set; } = new() { "x265", "h265", "hevc" };

    /// <summary>Sources autorisées (WEB, WEB-DL, WEBRip, BluRay, Remux…). Vide = toutes.</summary>
    public List<string> AllowedSources { get; set; } = new() { "WEB-DL", "WEB", "WEBRip", "BluRay", "Remux" };

    /// <summary>Termes qui, s'ils sont présents, rejettent la release (ex: "CAM", "TS").</summary>
    public List<string> RejectedTerms { get; set; } = new() { "CAM", "TS", "TELESYNC", "HDTS" };

    /// <summary>Nombre minimum de seeders pour considérer une release.</summary>
    public int MinSeeders { get; set; } = 1;

    public bool IsDefault { get; set; }
}
