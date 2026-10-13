using MediaHub.Core.Releases;

namespace MediaHub.Core.Abstractions;

public record C411AccountStats(string Username, long UploadedBytes, long DownloadedBytes, double Ratio);

/// <summary>
/// Client d'indexeur Torznab (C411). Renvoie des releases brutes à évaluer
/// ensuite par le <see cref="ReleaseParser"/>.
/// </summary>
public interface ITorznabClient
{
    Task<IReadOnlyList<ReleaseCandidate>> SearchForWatchAsync(MediaHub.Core.Entities.MediaItem media, CancellationToken ct = default, Func<string, Task>? progress = null) =>
        SearchAsync(SearchQuery.ForMedia(media.Title, media.Year, media.Type),
            SearchQuery.CategoriesForMedia(media.Type), media.Type != MediaType.Movie, forceRefresh: true, tmdbId: media.TmdbId, ct: ct);

    Task CheckConnectionAsync(CancellationToken ct = default);
    /// <param name="query">Terme de recherche (titre, éventuellement + saison/épisode).</param>
    /// <param name="categories">Catégories Torznab (ex: 2000 films, 5000 séries).</param>
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        string query,
        IEnumerable<int>? categories = null,
        bool tvSearch = false,
        bool forceRefresh = false,
        int? tmdbId = null,
        int? seasonNumber = null,
        CancellationToken ct = default);

    /// <summary>Lit les statistiques du compte C411 via son jeton d'API dédié.</summary>
    Task<C411AccountStats> GetAccountStatsAsync(CancellationToken ct = default);
}
