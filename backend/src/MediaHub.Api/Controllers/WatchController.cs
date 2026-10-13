using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Clients;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using MediaHub.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/watch")]
public class WatchController(AppDbContext db, ReleaseWatchService watch, IOptions<TelegramOptions> telegramOptions, TelegramClient telegram, WatchScanService scans, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<object> Status(CancellationToken ct)
    {
        var settings = await db.WatchSettings.AsNoTracking().SingleAsync(ct);
        var eligible = ReleaseWatchService.WatchedMedia(db).Select(value => value.Id);
        var watches = db.MediaWatches.AsNoTracking().Where(value => eligible.Contains(value.MediaItemId));
        return new
        {
            settings.Enabled, settings.IntervalMinutes, settings.MaxParallelSearches,
            TelegramConfigured = telegramOptions.Value.Configured,
            TotalMedia = await eligible.CountAsync(ct),
            InitializedMedia = await watches.CountAsync(value => value.InitializedAt != null, ct),
            LastCheckedAt = await watches.MaxAsync(value => value.LastCheckedAt, ct),
            Errors = await watches.CountAsync(value => value.LastError != null, ct),
            Pending = await db.ReleaseObservations.CountAsync(value => value.Decision == ReleaseDecision.Pending && eligible.Contains(value.MediaItemId), ct)
        };
    }

    [HttpPut]
    public async Task<IActionResult> Configure([FromBody] WatchConfiguration body, CancellationToken ct)
    {
        if (body.IntervalMinutes is < 60 or > 10080 || body.MaxParallelSearches is < 0 or > MediaHub.Core.Releases.WatchConcurrency.Maximum)
            return BadRequest("Frequence entre 60 et 10080 minutes, concurrence automatique (0) ou entre 1 et 32.");
        await db.WatchSettings.ExecuteUpdateAsync(update => update.SetProperty(value => value.Enabled, body.Enabled)
            .SetProperty(value => value.IntervalMinutes, body.IntervalMinutes)
            .SetProperty(value => value.MaxParallelSearches, body.MaxParallelSearches), ct);
        var now = DateTimeOffset.UtcNow;
        await db.MediaWatches.Where(value => value.LastCheckedAt != null).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.NextCheckAt, value => value.LastCheckedAt!.Value.AddMinutes(body.IntervalMinutes) < now
                ? now : value.LastCheckedAt!.Value.AddMinutes(body.IntervalMinutes)), ct);
        return NoContent();
    }

    [HttpPut("media/{id:guid}")]
    public async Task<ActionResult<MediaWatchStatus>> SetMediaEnabled(Guid id, [FromBody] MediaWatchConfiguration body, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(media => media.Id == id, ct)) return NotFound();
        await watch.SetEnabledAsync(id, body.Enabled, ct);
        return await watch.StatusAsync(id, ct);
    }

    [HttpPost("scans")]
    public async Task<IActionResult> StartScan(CancellationToken ct)
    {
        if (!await db.WatchSettings.AnyAsync(value => value.Enabled, ct))
            return Conflict("Active la veille globale avant de lancer un scan.");
        var scan = await scans.RequestAsync(true, ct);
        return scan is null ? Conflict("Aucun media eligible a surveiller.") : Accepted(new { scan.Id, scan.State });
    }

    [HttpGet("scans/current")]
    public async Task<object> CurrentScan([FromQuery] int page = 1, [FromQuery] string? filter = null, CancellationToken ct = default)
    {
        var scan = await db.WatchScans.AsNoTracking()
            .OrderByDescending(value => value.State != "completed" && value.State != "cancelled").ThenByDescending(value => value.CreatedAt).FirstOrDefaultAsync(ct);
        if (scan is null) return new { Scan = (object?)null };
        var query = db.WatchScanItems.AsNoTracking().Where(value => value.WatchScanId == scan.Id);
        var summary = await query.GroupBy(value => value.WatchScanId).Select(group => new
        {
            Total = group.Count(), Completed = group.Count(value => value.State == "completed"),
            Failed = group.Count(value => value.State == "failed"), Skipped = group.Count(value => value.State == "skipped"),
            Cancelled = group.Count(value => value.State == "cancelled"),
            Queued = group.Count(value => value.State == "queued"),
            Active = group.Count(value => value.FinishedAt == null && value.State != "queued"),
            NewProposals = group.Sum(value => value.NewProposals), LastActivityAt = group.Max(value => value.UpdatedAt)
        }).SingleAsync(ct);
        var activeItems = await query.Where(value => value.FinishedAt == null && value.State != "queued")
            .OrderBy(value => value.StartedAt).ThenBy(value => value.Id)
            .Select(value => new { value.Id, value.MediaItemId, value.Title, value.State, value.Detail,
                value.StartedAt, value.FinishedAt, value.UpdatedAt, value.Observations, value.Found, value.Filtered, value.NewProposals }).ToListAsync(ct);
        if (filter == "active") query = query.Where(value => value.FinishedAt == null && value.State != "queued");
        else if (filter is "queued" or "completed" or "failed" or "skipped" or "cancelled") query = query.Where(value => value.State == filter);
        var filteredTotal = await query.CountAsync(ct);
        var items = await query.OrderBy(value => value.FinishedAt != null).ThenBy(value => value.State == "queued")
            .ThenByDescending(value => value.UpdatedAt).ThenBy(value => value.Id)
            .Skip((Math.Max(1, page) - 1) * 25).Take(25)
            .Select(value => new { value.Id, value.MediaItemId, value.Title, value.State, value.Detail,
                value.StartedAt, value.FinishedAt, value.UpdatedAt, value.Observations, value.Found, value.Filtered, value.NewProposals }).ToListAsync(ct);
        var settings = await db.WatchSettings.AsNoTracking().SingleAsync(ct);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? workerLastSeen = null;
        var heartbeatPath = configuration["Pipeline:HeartbeatPath"];
        if (!string.IsNullOrWhiteSpace(heartbeatPath))
        {
            try
            {
                var heartbeat = await System.IO.File.ReadAllTextAsync(heartbeatPath, ct);
                if (DateTimeOffset.TryParse(heartbeat, out var lastSeen)) workerLastSeen = lastSeen;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new
        {
            Scan = new
            {
                scan.Id, scan.State, scan.Manual, scan.CreatedAt, scan.StartedAt, scan.FinishedAt, scan.RetryAt, scan.Detail,
                Summary = summary, ElapsedSeconds = scan.StartedAt.HasValue ? Math.Max(0, ((scan.FinishedAt ?? now) - scan.StartedAt.Value).TotalSeconds) : 0,
                MinimumRemainingSeconds = Math.Max(0, summary.Queued - 1) * MediaHub.Infrastructure.Clients.TorznabClient.RequestSpacingSeconds,
                settings.MaxParallelSearches, GlobalEnabled = settings.Enabled,
                WorkerOnline = workerLastSeen.HasValue && now - workerLastSeen.Value < TimeSpan.FromSeconds(60), WorkerLastSeen = workerLastSeen,
                PollSeconds = 2, RequestSpacingSeconds = MediaHub.Infrastructure.Clients.TorznabClient.RequestSpacingSeconds, RequestTimeoutSeconds = 120
            },
            FilteredTotal = filteredTotal, Page = Math.Max(1, page), PageSize = 25, Items = items, ActiveItems = activeItems
        };
    }

    [HttpPost("scans/{id:guid}/cancel")]
    public async Task<IActionResult> CancelScan(Guid id, CancellationToken ct) =>
        await scans.CancelAsync(id, ct) ? NoContent() : NotFound("Scan manuel introuvable.");

    [HttpGet("proposals")]
    public async Task<object> Proposals([FromQuery] ReleaseDecision decision = ReleaseDecision.Pending, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var query = db.ReleaseObservations.AsNoTracking().Where(value => value.Decision == decision);
        if (decision == ReleaseDecision.Pending)
        {
            var eligible = ReleaseWatchService.WatchedMedia(db).Select(value => value.Id);
            query = query.Where(value => eligible.Contains(value.MediaItemId));
        }
        var items = await query.OrderByDescending(value => value.FirstSeenAt).Skip((Math.Max(1, page) - 1) * 50).Take(50)
            .Select(value => new { value.Id, value.MediaItemId, MediaTitle = value.MediaItem!.Title, value.Title, value.SizeBytes,
                value.Coverage, Decision = value.Decision.ToString(), value.FirstSeenAt, value.ExpiresAt, value.NotifiedAt, value.JobId }).ToListAsync(ct);
        return new { Total = await query.CountAsync(ct), Items = items };
    }

    [HttpPost("proposals/{id:guid}/decision")]
    public async Task<object> Decide(Guid id, [FromBody] WatchDecision body, CancellationToken ct) =>
        new { Message = await watch.DecideAsync(id, body.Accept, ct) };

    [HttpPost("telegram/test")]
    public async Task<IActionResult> TestTelegram(CancellationToken ct)
    {
        if (!telegramOptions.Value.Configured) return BadRequest("Telegram non configure sur le serveur.");
        try { await telegram.SendAsync("MediaHub : notification de test.", null, ct); return NoContent(); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            var detail = error is InvalidOperationException && error.Message.StartsWith("Telegram HTTP ", StringComparison.Ordinal)
                ? error.Message : error.GetType().Name;
            return StatusCode(503, $"Echec du test Telegram ({detail}). Verifie le bot et le compte prive configure.");
        }
    }
}

public record WatchConfiguration(bool Enabled, int IntervalMinutes, int MaxParallelSearches);
public record WatchDecision(bool Accept);
public record MediaWatchConfiguration(bool Enabled);