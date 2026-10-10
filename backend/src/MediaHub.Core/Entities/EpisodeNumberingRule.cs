namespace MediaHub.Core.Entities;

/// <summary>Traduit la numérotation d'une saison de release vers TMDB.</summary>
public class EpisodeNumberingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public int ReleaseSeasonNumber { get; set; }
    public int TmdbSeasonNumber { get; set; }
    public int FirstTmdbEpisodeNumber { get; set; }
}