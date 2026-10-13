using System.Text.Json;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Services;

public class ReleaseWatchService(AppDbContext db, ITorznabClient indexer)
{
    public async Task<IReadOnlyList<ReleaseCandidate>> SavedReleasesAsync(MediaItem media, CancellationToken ct)
    {
        if (media.ReleaseSnapshotJson is { } snapshot)
            return JsonSerializer.Deserialize<List<ReleaseCandidate>>(snapshot) ?? [];
        var observations = await db.ReleaseObservations.AsNoTracking()
            .Where(value => value.MediaItemId == media.Id)
            .ToListAsync(ct);
        var latest = await db.MediaWatches.Where(value => value.MediaItemId == media.Id)
            .Select(value => value.LastCheckedAt).FirstOrDefaultAsync(ct)
            ?? (observations.Count == 0 ? (DateTimeOffset?)null : observations.Max(value => value.LastSeenAt));
        media.ReleasesCheckedAt = latest;
        return observations.Where(value => value.LastSeenAt == latest).Select(value =>
        {
            var release = new ReleaseCandidate { Title = value.Title, DownloadUrl = value.DownloadUrl, SizeBytes = value.SizeBytes };
            ReleaseParser.Parse(release);
            return release;
        }).ToList();
    }

    public async Task SaveReleasesAsync(Guid mediaId, IReadOnlyList<ReleaseCandidate> releases, CancellationToken ct)
    {
        var snapshot = JsonSerializer.Serialize(releases);
        var now = DateTimeOffset.UtcNow;
        await db.MediaItems.Where(media => media.Id == mediaId).ExecuteUpdateAsync(update => update
            .SetProperty(media => media.ReleaseSnapshotJson, snapshot)
            .SetProperty(media => media.ReleasesCheckedAt, now), ct);
    }

    public async Task<MediaWatchStatus> StatusAsync(Guid mediaId, CancellationToken ct)
    {
        var settings = await db.WatchSettings.AsNoTracking().SingleAsync(ct);
        var enabled = await db.MediaItems.Where(media => media.Id == mediaId).Select(media => media.WatchEnabled).SingleAsync(ct);
        if (!await EligibleMedia(db).AnyAsync(media => media.Id == mediaId, ct))
            return new("excluded", settings.IntervalMinutes, null, null, null, enabled, settings.Enabled);
        var watch = await db.MediaWatches.AsNoTracking().FirstOrDefaultAsync(value => value.MediaItemId == mediaId, ct);
        var state = !settings.Enabled || !enabled ? "disabled" : watch?.LastError != null ? "error" :
            watch?.InitializedAt == null ? "initializing" : "active";
        return new(state, settings.IntervalMinutes, watch?.LastCheckedAt,
            settings.Enabled && enabled ? watch?.NextCheckAt : null, watch?.LastError, enabled, settings.Enabled);
    }

    public static IQueryable<MediaItem> WatchedMedia(AppDbContext db) => EligibleMedia(db).Where(media => media.WatchEnabled);

    public static IQueryable<MediaItem> EligibleMedia(AppDbContext db) => db.MediaItems.Where(media =>
        media.Type != MediaType.Movie || (string.IsNullOrWhiteSpace(media.StoragePath) &&
            !db.CloudFiles.Any(file => file.MediaItemId == media.Id) &&
            !db.Episodes.Any(episode => episode.Season!.MediaItemId == media.Id && !string.IsNullOrWhiteSpace(episode.CloudPath))));

    public async Task SetEnabledAsync(Guid mediaId, bool enabled, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.MediaItems.Where(media => media.Id == mediaId)
            .ExecuteUpdateAsync(update => update.SetProperty(media => media.WatchEnabled, enabled), ct);
        if (!enabled)
            await db.ReleaseObservations.Where(value => value.MediaItemId == mediaId && value.Decision == ReleaseDecision.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Decision, ReleaseDecision.Expired), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<ReleaseWatchScanResult?> ScanAsync(Guid mediaId, CancellationToken ct, Func<string, Task>? progress = null)
    {
        if (!await WatchedMedia(db).AnyAsync(media => media.Id == mediaId, ct)) return null;
        var watch = await db.MediaWatches.Include(value => value.MediaItem)
            .FirstOrDefaultAsync(value => value.MediaItemId == mediaId, ct);
        if (watch?.MediaItem is not { } media) return null;
        var settings = await db.WatchSettings.SingleAsync(ct);
        if (!settings.Enabled) return null;
        var releases = await indexer.SearchForWatchAsync(media, ct, progress);
        if (!await WatchedMedia(db).AnyAsync(value => value.Id == mediaId, ct) ||
            !await db.WatchSettings.AsNoTracking().AnyAsync(value => value.Enabled, ct)) return null;
        if (progress is not null) await progress("processing");
        var profile = await ProfileAsync(media, ct);
        var now = DateTimeOffset.UtcNow;
        var existing = await db.ReleaseObservations.Where(value => value.MediaItemId == mediaId)
            .ToDictionaryAsync(value => value.Fingerprint, ct);
        var matching = releases.Where(value => ReleaseWatchPolicy.Matches(media, value)).ToList();
        media.ReleaseSnapshotJson = JsonSerializer.Serialize(matching);
        media.ReleasesCheckedAt = now;
        var filtered = 0;
        var newProposals = 0;
        var baseline = !watch.InitializedAt.HasValue;
        foreach (var release in matching)
        {
            var key = ReleaseWatchPolicy.Fingerprint(release);
            var eligible = ReleaseParser.Evaluate(release, profile).Accepted;
            if (!eligible) filtered++;
            if (existing.TryGetValue(key, out var observation))
            {
                observation.LastSeenAt = now;
                if (!string.IsNullOrWhiteSpace(release.DownloadUrl)) observation.DownloadUrl = release.DownloadUrl;
                if (observation.Decision == ReleaseDecision.Filtered && eligible)
                {
                    observation.Decision = ReleaseDecision.Pending;
                    observation.ExpiresAt = now.AddDays(3);
                    observation.NextNotificationAt = now;
                    newProposals++;
                }
                continue;
            }
            observation = new ReleaseObservation
            {
                MediaItemId = mediaId, Fingerprint = key, Title = release.Title,
                DownloadUrl = release.DownloadUrl ?? string.Empty, SizeBytes = release.SizeBytes,
                Coverage = ReleaseParser.DescribeCoverage(release) ?? string.Empty,
                Decision = ReleaseWatchPolicy.InitialDecision(watch.InitializedAt.HasValue, eligible),
                FirstSeenAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(3), NextNotificationAt = now
            };
            db.ReleaseObservations.Add(observation);
            if (observation.Decision == ReleaseDecision.Pending) newProposals++;
            existing.Add(key, observation);
        }
        watch.InitializedAt ??= now;
        watch.LastCheckedAt = now;
        watch.NextCheckAt = now.AddMinutes(settings.IntervalMinutes);
        watch.LastError = null;
        await db.SaveChangesAsync(ct);
        return new ReleaseWatchScanResult(releases.Count, matching.Count, filtered, newProposals, baseline);
    }

    public async Task<string> DecideAsync(Guid id, bool accept, CancellationToken ct)
    {
        var observation = await db.ReleaseObservations.AsNoTracking().Include(value => value.MediaItem)
            .FirstOrDefaultAsync(value => value.Id == id, ct);
        if (observation?.MediaItem is not { } media) return "Proposition introuvable.";
        if (observation.Decision != ReleaseDecision.Pending) return "Proposition deja traitee.";
        if (accept && !await WatchedMedia(db).AnyAsync(value => value.Id == media.Id, ct))
            return "Veille desactivee pour ce media.";
        var now = DateTimeOffset.UtcNow;
        if (accept && observation.ExpiresAt <= now)
        {
            await db.ReleaseObservations.Where(value => value.Id == id && value.Decision == ReleaseDecision.Pending)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Decision, ReleaseDecision.Expired), ct);
            return "Proposition expiree.";
        }

        ReleaseCandidate? current = null;
        if (accept)
        {
            var releases = await indexer.SearchForWatchAsync(media, ct);
            current = releases.FirstOrDefault(value => ReleaseWatchPolicy.Fingerprint(value) == observation.Fingerprint);
            var profile = await ProfileAsync(media, ct);
            if (current is null ||
                !ReleaseWatchPolicy.Matches(media, current) || !ReleaseParser.Evaluate(current, profile).Accepted)
                return "Release indisponible ou incompatible avec le profil actuel.";
            if (string.IsNullOrWhiteSpace(current.DownloadUrl))
            {
                var downloadable = await indexer.SearchAsync(current.Title,
                    categories: SearchQuery.CategoriesForMedia(media.Type), tvSearch: media.Type != MediaType.Movie,
                    forceRefresh: true, ct: ct);
                current = downloadable.FirstOrDefault(value => ReleaseWatchPolicy.Fingerprint(value) == observation.Fingerprint);
                if (current is null || string.IsNullOrWhiteSpace(current.DownloadUrl))
                    return "Lien de telechargement indisponible. La proposition reste en attente ; reessaie plus tard.";
                if (!ReleaseWatchPolicy.Matches(media, current) || !ReleaseParser.Evaluate(current, profile).Accepted)
                    return "Release indisponible ou incompatible avec le profil actuel.";
            }
            if (string.IsNullOrWhiteSpace(current.DownloadUrl))
                return "Lien de telechargement indisponible. La proposition reste en attente ; reessaie plus tard.";
        }

        now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var jobId = accept ? (Guid?)Guid.NewGuid() : null;
        var changed = await db.ReleaseObservations.Where(value => value.Id == id && value.Decision == ReleaseDecision.Pending &&
                (!accept || value.ExpiresAt > now))
            .ExecuteUpdateAsync(update => update
                .SetProperty(value => value.Decision, accept ? ReleaseDecision.Accepted : ReleaseDecision.Blacklisted)
                .SetProperty(value => value.DecidedAt, now)
                .SetProperty(value => value.JobId, jobId), ct);
        if (changed == 0) return "Proposition deja traitee.";
        if (accept)
        {
            var rule = current!.SeasonNumber is { } releaseSeason
                ? await db.EpisodeNumberingRules.FirstOrDefaultAsync(value => value.MediaItemId == media.Id && value.ReleaseSeasonNumber == releaseSeason, ct)
                : null;
            var payload = new ReleasePayload(current.DownloadUrl, current.Title, null, null, null,
                ReleaseSeasonNumber: rule?.ReleaseSeasonNumber, TmdbSeasonNumber: rule?.TmdbSeasonNumber,
                FirstTmdbEpisodeNumber: rule?.FirstTmdbEpisodeNumber);
            db.PipelineJobs.Add(new PipelineJob { Id = jobId!.Value, MediaItemId = media.Id, Kind = JobKind.Download, PayloadJson = JsonSerializer.Serialize(payload) });
            await db.MediaItems.Where(value => value.Id == media.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, MediaStatus.Processing), ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return accept ? "Accepte : ajoute a la pipeline." : "Blackliste : cette release ne sera plus proposee.";
    }

    public async Task<ReleaseProfile> ProfileAsync(MediaItem media, CancellationToken ct) =>
        (media.ReleaseProfileId.HasValue ? await db.ReleaseProfiles.FindAsync([media.ReleaseProfileId.Value], ct) : null)
        ?? await db.ReleaseProfiles.FirstOrDefaultAsync(value => value.IsDefault, ct) ?? new ReleaseProfile();
}