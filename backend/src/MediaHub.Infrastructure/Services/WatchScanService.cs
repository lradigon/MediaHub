using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Services;

public class WatchScanService(AppDbContext db)
{
    public async Task<bool> CancelAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(411060)", ct);
        var scan = await db.WatchScans.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id && value.Manual, ct);
        if (scan is null) return false;
        if (scan.State is "completed" or "cancelled") return true;
        var now = DateTimeOffset.UtcNow;
        await db.WatchScans.Where(value => value.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.State, "cancelled").SetProperty(value => value.FinishedAt, now)
            .SetProperty(value => value.Detail, "Scan manuel annule"), ct);
        await db.WatchScanItems.Where(value => value.WatchScanId == id && value.FinishedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.State, "cancelled")
                .SetProperty(value => value.Detail, "Annule par l'utilisateur").SetProperty(value => value.FinishedAt, now)
                .SetProperty(value => value.UpdatedAt, now), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<WatchScan?> RequestAsync(bool manual, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(411060)", ct);
        var current = await db.WatchScans.FirstOrDefaultAsync(value => value.State == "queued" || value.State == "running" || value.State == "paused", ct);
        if (current is not null) return current;
        var settings = await db.WatchSettings.AsNoTracking().SingleAsync(ct);
        if (!settings.Enabled) return null;
        var media = ReleaseWatchService.WatchedMedia(db);
        var now = DateTimeOffset.UtcNow;
        if (!manual)
            media = media.Where(value => !db.MediaWatches.Any(watch => watch.MediaItemId == value.Id) ||
                db.MediaWatches.Any(watch => watch.MediaItemId == value.Id && watch.NextCheckAt <= now));
        var candidates = await media.OrderBy(value => value.Title).Select(value => new { value.Id, value.Title }).ToListAsync(ct);
        if (candidates.Count == 0) return null;
        var cooldown = await db.WatchScans.Where(value => value.RetryAt > now &&
                (value.State == "paused" || value.State == "cancelled"))
            .OrderByDescending(value => value.RetryAt).Select(value => new { value.RetryAt, value.Detail }).FirstOrDefaultAsync(ct);
        var scan = new WatchScan { Manual = manual, RetryAt = cooldown?.RetryAt,
            State = cooldown is null ? "queued" : "paused", Detail = cooldown is null ? null : "Suspension C411 conservee apres annulation",
            Items = candidates.Select(value => new WatchScanItem
            { MediaItemId = value.Id, Title = value.Title }).ToList() };
        db.WatchScans.Add(scan);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return scan;
    }
}