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

public record EpisodeSyncSummary(int Seasons, int Total, int Available, int Missing, int Unaired);

/// <summary>
/// Rapproche les épisodes présents sur le disque (fichiers « S01 E02 … ») de la
/// liste TMDB pour marquer chaque épisode : présent (Available), manquant (Missing)
/// ou pas encore diffusé (Unaired).
/// </summary>
public partial class EpisodeSyncService(
    AppDbContext db,
    ITmdbClient tmdb,
    IOptions<LibraryOptions> libraryOptions,
    ILogger<EpisodeSyncService> logger)
{
    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"];

    [GeneratedRegex(@"[Ss](\d{1,3})\s*[Ee](\d{1,4})(?:\s*-\s*(\d{1,4}))?")]
    private static partial Regex EpisodeFileRegex();

    public async Task<EpisodeSyncSummary> SyncAsync(Guid mediaItemId, CancellationToken ct)
    {
        // FindAsync regarde d'abord les entités déjà suivies (utile quand le scan
        // vient de créer le média sans encore l'avoir persisté).
        var media = await db.MediaItems.FindAsync([mediaItemId], ct)
            ?? throw new InvalidOperationException($"Média {mediaItemId} introuvable.");

        if (media.TmdbId is not { } tmdbId)
            throw new InvalidOperationException("Média sans TmdbId : impossible de récupérer les épisodes.");

        if (!string.IsNullOrWhiteSpace(media.StoragePath))
            StorageAvailability.EnsureAvailable(libraryOptions.Value);
        if (!Directory.Exists(media.StoragePath))
        {
            var resolvedStorage = ResolveRenamedStoragePath(media);
            if (resolvedStorage is not null)
            {
                media.StoragePath = resolvedStorage.Value.Path;
                media.Type = resolvedStorage.Value.Type;
                logger.LogInformation("Dossier de stockage mis à jour pour {Title} : {Path}", media.Title, resolvedStorage.Value.Path);
            }
        }
        var numberingRules = await db.EpisodeNumberingRules
            .Where(rule => rule.MediaItemId == mediaItemId)
            .ToListAsync(ct);
        var onDisk = ScanDisk(media.StoragePath, numberingRules);
        var tmdbSeasons = await tmdb.GetSeasonsAsync(tmdbId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        var seasons = await db.Seasons.Where(s => s.MediaItemId == mediaItemId).ToListAsync(ct);
        var specialSeasons = seasons.Where(s => s.SeasonNumber == 0).ToList();
        db.Seasons.RemoveRange(specialSeasons);
        seasons = seasons.Where(s => s.SeasonNumber != 0).ToList();
        var seasonIds = seasons.Select(s => s.Id).ToList();
        var episodes = await db.Episodes.Where(e => seasonIds.Contains(e.SeasonId)).ToListAsync(ct);

        int total = 0, available = 0, missing = 0, unaired = 0;

        foreach (var ts in tmdbSeasons.Where(season => season.SeasonNumber != 0))
        {
            var season = seasons.FirstOrDefault(s => s.SeasonNumber == ts.SeasonNumber);
            if (season is null)
            {
                season = new Season { MediaItemId = mediaItemId, SeasonNumber = ts.SeasonNumber, Title = ts.Name };
                // db.Add force l'état « Added » (évite que la clé Guid pré-remplie soit prise pour un update).
                db.Seasons.Add(season);
                seasons.Add(season);
            }
            else
            {
                season.Title = ts.Name;
            }

            foreach (var te in ts.Episodes)
            {
                total++;
                var ep = episodes.FirstOrDefault(e => e.SeasonId == season.Id && e.EpisodeNumber == te.EpisodeNumber);
                if (ep is null)
                {
                    ep = new Episode { SeasonId = season.Id, EpisodeNumber = te.EpisodeNumber };
                    db.Episodes.Add(ep);
                    episodes.Add(ep);
                }

                ep.Title = te.Name;
                ep.AirDate = te.AirDate;

                // On ne réécrit pas un épisode déjà en cours de traitement dans la pipeline.
                if (ep.Status != EpisodeStatus.Processing)
                {
                    if (onDisk.Contains((ts.SeasonNumber, te.EpisodeNumber)) || !string.IsNullOrWhiteSpace(ep.CloudPath)) ep.Status = EpisodeStatus.Available;
                    else if (te.AirDate is { } d && d <= today) ep.Status = EpisodeStatus.Missing;
                    else ep.Status = EpisodeStatus.Unaired;
                }

                if (ep.Status == EpisodeStatus.Available) available++;
                else if (ep.Status == EpisodeStatus.Missing) missing++;
                else if (ep.Status == EpisodeStatus.Unaired) unaired++;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Sync épisodes « {Title} » : {Available} présents, {Missing} manquants, {Unaired} à venir (sur {Total}).",
            media.Title, available, missing, unaired, total);
        return new EpisodeSyncSummary(tmdbSeasons.Count(season => season.SeasonNumber != 0), total, available, missing, unaired);
    }

    private (string Path, MediaType Type)? ResolveRenamedStoragePath(MediaItem media)
    {
        if (string.IsNullOrWhiteSpace(media.StoragePath)) return null;

        var categories = new[]
        {
            ("Séries", MediaType.Series),
            ("Animés", MediaType.Anime),
            ("Dessin Animés", MediaType.Anime)
        };
        var title = media.Title.Trim();
        var yearPrefix = media.Year is { } year ? $"{title} - {year}" : $"{title} - ";
        var previousFolderName = Path.GetFileName(media.StoragePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var previousFolderPrefix = Regex.Replace(previousFolderName, @"(?<=\d{4})\s*-\s*(?:\d{4}|présent)$", string.Empty);

        foreach (var (category, type) in categories)
        {
            var categoryPath = Path.Combine(libraryOptions.Value.StorageRoot, category);
            if (!Directory.Exists(categoryPath)) continue;

            var match = Directory.EnumerateDirectories(categoryPath)
                .FirstOrDefault(path =>
                {
                    var folderName = Path.GetFileName(path);
                    return folderName.StartsWith(yearPrefix, StringComparison.OrdinalIgnoreCase) ||
                        folderName.StartsWith(previousFolderPrefix, StringComparison.OrdinalIgnoreCase);
                });
            if (match is not null) return (match, type);
        }

        return null;
    }

    private static HashSet<(int Season, int Episode)> ScanDisk(
        string? storagePath, IReadOnlyList<EpisodeNumberingRule> numberingRules)
    {
        var set = new HashSet<(int, int)>();
        if (string.IsNullOrWhiteSpace(storagePath) || !Directory.Exists(storagePath))
            return set;

        foreach (var file in Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories))
        {
            if (!VideoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;
            var m = EpisodeFileRegex().Match(Path.GetFileName(file));
            if (m.Success)
            {
                var season = int.Parse(m.Groups[1].Value);
                var firstEpisode = int.Parse(m.Groups[2].Value);
                var lastEpisode = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : firstEpisode;

                if (lastEpisode < firstEpisode)
                    (firstEpisode, lastEpisode) = (lastEpisode, firstEpisode);

                var rule = numberingRules.FirstOrDefault(item => item.ReleaseSeasonNumber == season);
                for (var episode = firstEpisode; episode <= lastEpisode; episode++)
                {
                    var mappedSeason = rule?.TmdbSeasonNumber ?? season;
                    var mappedEpisode = rule is null ? episode : rule.FirstTmdbEpisodeNumber + episode - 1;
                    set.Add((mappedSeason, mappedEpisode));
                }
            }
        }
        return set;
    }
}
