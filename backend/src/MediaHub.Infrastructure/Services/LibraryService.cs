using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Services;

/// <summary>
/// Logique d'ajout d'un média à la bibliothèque depuis TMDB, mutualisée entre
/// l'ajout direct (admin) et l'approbation d'une requête (ami).
/// </summary>
public class LibraryService(AppDbContext db, ITmdbClient tmdb)
{
    public async Task<MediaItem> AddFromTmdbAsync(int tmdbId, MediaType type, CancellationToken ct = default)
    {
        var existing = await db.MediaItems
            .FirstOrDefaultAsync(m => m.TmdbId == tmdbId && m.Type == type, ct);
        if (existing is not null) return existing;

        var info = type == MediaType.Movie
            ? await tmdb.GetMovieAsync(tmdbId, ct)
            : await tmdb.GetTvAsync(tmdbId, ct);

        if (info is null)
            throw new InvalidOperationException($"TMDB {tmdbId} introuvable.");

        var item = new MediaItem
        {
            TmdbId = tmdbId,
            Type = type,
            Title = info.Title,
            OriginalTitle = info.OriginalTitle,
            OriginalLanguage = info.OriginalLanguage,
            Overview = info.Overview,
            Year = info.Year,
            ReleaseDate = info.ReleaseDate,
            PosterPath = info.PosterPath,
            BackdropPath = info.BackdropPath,
            Status = MediaStatus.Wanted
        };
        db.MediaItems.Add(item);

        // Met en file une recherche de release pour ce média.
        db.PipelineJobs.Add(new PipelineJob
        {
            Kind = JobKind.Search,
            MediaItemId = item.Id
        });

        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>Actualise titres, résumés et visuels des médias existants depuis TMDB.</summary>
    public async Task RefreshMetadataAsync(PipelineJob job, CancellationToken ct = default)
    {
        var mediaItems = await db.MediaItems.OrderBy(m => m.Title).ToListAsync(ct);
        for (var index = 0; index < mediaItems.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var media = mediaItems[index];
            if (media.TmdbId is not { } tmdbId) continue;

            var info = media.Type == MediaType.Movie
                ? await tmdb.GetMovieAsync(tmdbId, ct)
                : await tmdb.GetTvAsync(tmdbId, ct);
            if (info is not null)
            {
                media.Title = info.Title;
                media.OriginalTitle = info.OriginalTitle;
                media.OriginalLanguage = info.OriginalLanguage;
                media.Overview = info.Overview;
                media.Year = info.Year;
                media.ReleaseDate = info.ReleaseDate;
                media.PosterPath = info.PosterPath;
                media.BackdropPath = info.BackdropPath;
            }

            if ((index + 1) % 10 == 0)
            {
                await db.SaveChangesAsync(ct);
                job.Progress = (index + 1) * 100d / Math.Max(1, mediaItems.Count);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
