using System.Text.RegularExpressions;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Services;

public record ScanSummary(int Total, int Added, int Skipped, int Unmatched);

/// <summary>
/// Parcourt le stockage existant (lecteur monté) et peuple la bibliothèque.
/// Les séries/animés sont détectés par dossier de titre ; les films sont détectés
/// fichier par fichier (à la racine de « Films » ou dans les sous-dossiers de
/// collection). Chaque titre est rapproché de TMDB et créé en Available.
/// </summary>
public partial class StorageScanService(
    AppDbContext db,
    ITmdbClient tmdb,
    EpisodeSyncService episodeSync,
    IOptions<LibraryOptions> options,
    ILogger<StorageScanService> logger)
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"];

    /// <summary>Catégories reconnues : type de média et mode de scan (par fichier pour les films).
    /// Les séries/animés sont traités en premier (sync des épisodes), les films ensuite.</summary>
    private static readonly (string Folder, MediaType Type, bool ByFile)[] Categories =
    [
        ("Séries", MediaType.Series, false),
        ("Animés", MediaType.Anime, false),
        ("Dessin Animés", MediaType.Anime, false),
        ("Films", MediaType.Movie, true),
        ("Reportages", MediaType.Movie, true),
    ];

    [GeneratedRegex(@"\s*-\s*\d{4}.*$")]
    private static partial Regex YearSuffixRegex();

    [GeneratedRegex(@"\s*\(\d{4}\)\s*$")]
    private static partial Regex ParenthesizedYearSuffixRegex();

    /// <summary>Préfixe de numérotation de collection : « 39 - », « 1 - »…</summary>
    [GeneratedRegex(@"^\d{1,3}\s*-\s*")]
    private static partial Regex CollectionPrefixRegex();

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    private sealed record ScanItem(MediaType Type, string Path, bool IsFile);

    public async Task<ScanSummary> ScanAsync(PipelineJob? job, CancellationToken ct)
    {
        var root = options.Value.StorageRoot;
        StorageAvailability.EnsureAvailable(options.Value);

        var mediaWithStorage = await db.MediaItems
            .Where(media => media.StoragePath != null && media.StoragePath != string.Empty)
            .ToListAsync(ct);
        var removedFromStorage = 0;
        foreach (var media in mediaWithStorage)
        {
            if (File.Exists(media.StoragePath) || Directory.Exists(media.StoragePath)) continue;

            media.StoragePath = null;
            if (media.Status != MediaStatus.Ignored)
                media.Status = MediaStatus.Wanted;
            removedFromStorage++;
        }
        if (removedFromStorage > 0)
            logger.LogInformation("Scan: {Count} média(s) retiré(s) du stock local.", removedFromStorage);

        var items = new List<ScanItem>();
        foreach (var (folder, type, byFile) in Categories)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;

            if (byFile)
            {
                // Films : chaque fichier vidéo (racine + sous-dossiers de collection) = un film.
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                        items.Add(new ScanItem(type, file, true));
            }
            else
            {
                // Séries/animés : chaque sous-dossier = une série.
                foreach (var d in Directory.GetDirectories(dir))
                    items.Add(new ScanItem(type, d, false));
            }
        }

        var known = (await db.MediaItems.Where(m => m.TmdbId != null).ToListAsync(ct))
            .GroupBy(media => (media.TmdbId!.Value, media.Type == MediaType.Movie))
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(media => !string.IsNullOrWhiteSpace(media.StoragePath)).First());
        var knownStoragePaths = mediaWithStorage
            .Where(media => !string.IsNullOrWhiteSpace(media.StoragePath))
            .GroupBy(media => NormalizeStoragePath(media.StoragePath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        int total = items.Count, done = 0, added = 0, skipped = 0, unmatched = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var (title, year) = item.IsFile
                ? ParseMovieFile(Path.GetFileName(item.Path))
                : (CleanTitle(Path.GetFileName(item.Path)), FolderYear(Path.GetFileName(item.Path)));

            MediaItem? series = null;

            try
            {
                if (knownStoragePaths.TryGetValue(NormalizeStoragePath(item.Path), out var storedMedia))
                {
                    if (!item.IsFile) series = storedMedia;
                    skipped++;
                }
                else
                {
                    var results = await tmdb.SearchAsync(title, ct);
                    var match = PickMatch(results, item.Type, year, title);

                    if (match is null)
                    {
                        unmatched++;
                        logger.LogInformation("Scan: aucun résultat TMDB pour « {Title} »", title);
                    }
                    else if (known.TryGetValue((match.TmdbId, item.Type == MediaType.Movie), out var existing))
                    {
                        if (existing.StoragePath is null ||
                            (!item.IsFile && !ContainsVideoFiles(existing.StoragePath) && ContainsVideoFiles(item.Path)))
                        {
                            if (existing.StoragePath is not null)
                                knownStoragePaths.Remove(NormalizeStoragePath(existing.StoragePath));
                            existing.StoragePath = item.Path;
                        }
                        existing.Type = item.Type;
                        knownStoragePaths[NormalizeStoragePath(existing.StoragePath!)] = existing;
                        if (!item.IsFile) series = existing;
                        skipped++;
                    }
                    else
                    {
                        var created = new MediaItem
                        {
                            TmdbId = match.TmdbId,
                            Type = item.Type,
                            Title = match.Title,
                            OriginalTitle = match.OriginalTitle,
                            OriginalLanguage = match.OriginalLanguage,
                            Overview = match.Overview,
                            Year = match.Year,
                            PosterPath = match.PosterPath,
                            BackdropPath = match.BackdropPath,
                            Status = MediaStatus.Available,
                            StoragePath = item.Path
                        };
                        db.MediaItems.Add(created);
                        known[(match.TmdbId, item.Type == MediaType.Movie)] = created;
                        knownStoragePaths[NormalizeStoragePath(created.StoragePath!)] = created;
                        if (!item.IsFile) series = created;
                        added++;
                    }
                }
            }
            catch (Exception ex)
            {
                unmatched++;
                logger.LogWarning(ex, "Scan: échec du rapprochement pour « {Title} »", title);
            }

            // Pour une série/animé, on synchronise aussi les épisodes (présents vs TMDB).
            if (series is not null)
            {
                try
                {
                    await episodeSync.SyncAsync(series.Id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Scan: sync des épisodes échouée pour « {Title} »", series.Title);
                }
            }

            done++;

            // Persiste par lots + met à jour la progression (sans écraser le statut du job).
            if (done % 5 == 0)
            {
                await db.SaveChangesAsync(ct);
                if (job is not null)
                    await db.PipelineJobs.Where(j => j.Id == job.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.Progress, done * 100d / Math.Max(1, total)), ct);
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Scan terminé : {Added} ajoutés, {Skipped} déjà présents, {Unmatched} non reconnus (sur {Total}).",
            added, skipped, unmatched, total);
        return new ScanSummary(total, added, skipped, unmatched);
    }

    /// <summary>Choisit la meilleure correspondance TMDB selon le type attendu et l'année.</summary>
    private static TmdbSearchItem? PickMatch(IReadOnlyList<TmdbSearchItem> results, MediaType type, int? year, string title)
    {
        var wantMovie = type == MediaType.Movie;
        var sameKind = results.Where(r => (r.Type == MediaType.Movie) == wantMovie).ToList();
        var exactTitle = sameKind.Where(result =>
            string.Equals(result.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(result.OriginalTitle?.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactTitle.Count > 0)
            return exactTitle.FirstOrDefault(result => year is null || result.Year == year) ?? exactTitle[0];
        if (title.Trim().Length <= 2)
            return null;

        if (year is { } y)
        {
            var exact = sameKind.FirstOrDefault(r => r.Year == y)
                        ?? sameKind.FirstOrDefault(r => r.Year is { } ry && Math.Abs(ry - y) <= 1);
            if (exact is not null) return exact;
        }

        return sameKind.FirstOrDefault();
    }

    /// <summary>Extrait titre + année d'un nom de fichier film (« 39 - Titre - 2022 - Réalisateur.mkv »).</summary>
    private static (string Title, int? Year) ParseMovieFile(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = CollectionPrefixRegex().Replace(name, string.Empty);

        var ym = YearRegex().Match(name);
        int? year = ym.Success ? int.Parse(ym.Value) : null;

        var title = (ym.Success ? name[..ym.Index] : name).Trim().Trim('-').Trim();
        if (string.IsNullOrWhiteSpace(title)) title = name.Trim();
        return (title, year);
    }

    private static string CleanTitle(string folder)
    {
        // Retire un suffixe d'année façon « - 2008-2013 » ou « - 2012-présent ».
        var title = YearSuffixRegex().Replace(folder, string.Empty).Trim();
        return ParenthesizedYearSuffixRegex().Replace(title, string.Empty).Trim();
    }

    private static int? FolderYear(string folder)
    {
        var match = Regex.Match(folder, @"(?:\s*-\s*|\s*\()(?<year>(?:19|20)\d{2})(?:\s*[-)]|$)");
        return match.Success ? int.Parse(match.Groups["year"].Value) : null;
    }

    private static string NormalizeStoragePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool ContainsVideoFiles(string path) =>
        Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Any(file => VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));
}
