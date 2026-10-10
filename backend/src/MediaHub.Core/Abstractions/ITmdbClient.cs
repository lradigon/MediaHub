namespace MediaHub.Core.Abstractions;

/// <summary>Résultat de recherche TMDB (film ou série).</summary>
public record TmdbSearchItem(
    int TmdbId,
    MediaType Type,
    string Title,
    string? OriginalTitle,
    string? OriginalLanguage,
    int? Year,
    DateOnly? ReleaseDate,
    string? Overview,
    string? PosterPath,
    string? BackdropPath);

public record TmdbEpisodeInfo(
    int SeasonNumber,
    int EpisodeNumber,
    string? Name,
    DateOnly? AirDate);

public record TmdbSeason(
    int SeasonNumber,
    string? Name,
    IReadOnlyList<TmdbEpisodeInfo> Episodes);

/// <summary>Accès aux métadonnées TMDB.</summary>
public interface ITmdbClient
{
    Task CheckConnectionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TmdbSearchItem>> SearchAsync(string query, CancellationToken ct = default);
    Task<TmdbSearchItem?> GetMovieAsync(int tmdbId, CancellationToken ct = default);
    /// <summary>Réalisateur principal d'un film, utilisé pour le nommage de la bibliothèque.</summary>
    Task<string?> GetMovieDirectorAsync(int tmdbId, CancellationToken ct = default);
    Task<TmdbSearchItem?> GetTvAsync(int tmdbId, CancellationToken ct = default);
    Task<TmdbEpisodeInfo?> GetEpisodeAsync(int tvId, int season, int episode, CancellationToken ct = default);
    /// <summary>Toutes les saisons d'une série avec leurs épisodes (numéro, nom, date de diffusion).</summary>
    Task<IReadOnlyList<TmdbSeason>> GetSeasonsAsync(int tvId, CancellationToken ct = default);
}
