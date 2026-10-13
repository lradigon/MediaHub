using System.Net;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Worker;

public class ReleaseWatchWorker(IServiceScopeFactory scopes, ILogger<ReleaseWatchWorker> logger) : BackgroundService
{
    private bool recovered;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Watch scheduler failed ({ErrorType})", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!recovered)
        {
            await db.WatchScanItems.Where(value => value.FinishedAt == null && value.State != "queued")
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.State, "queued")
                    .SetProperty(value => value.Detail, "Reprise apres redemarrage du worker")
                    .SetProperty(value => value.StartedAt, (DateTimeOffset?)null), ct);
            recovered = true;
        }
        var settings = await db.WatchSettings.AsNoTracking().SingleAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await db.ReleaseObservations.Where(value => value.Decision == MediaHub.Core.Entities.ReleaseDecision.Pending && value.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Decision, MediaHub.Core.Entities.ReleaseDecision.Expired), ct);
        if (!settings.Enabled) return;
        var eligible = ReleaseWatchService.WatchedMedia(db).Select(value => value.Id);
        var stockEligible = ReleaseWatchService.EligibleMedia(db).Select(value => value.Id);
        await db.MediaWatches.Where(value => !stockEligible.Contains(value.MediaItemId)).ExecuteDeleteAsync(ct);
        await db.ReleaseObservations.Where(value => value.Decision == MediaHub.Core.Entities.ReleaseDecision.Pending && !eligible.Contains(value.MediaItemId))
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Decision, MediaHub.Core.Entities.ReleaseDecision.Expired), ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "MediaWatches" ("MediaItemId", "NextCheckAt")
            SELECT m."Id", now() FROM "MediaItems" m
            WHERE m."WatchEnabled" AND (m."Type" <> 0 OR (
                (m."StoragePath" IS NULL OR btrim(m."StoragePath") = '')
                AND NOT EXISTS (SELECT 1 FROM "CloudFiles" f WHERE f."MediaItemId" = m."Id")
                AND NOT EXISTS (SELECT 1 FROM "Episodes" e JOIN "Seasons" s ON e."SeasonId" = s."Id"
                    WHERE s."MediaItemId" = m."Id" AND e."CloudPath" IS NOT NULL AND btrim(e."CloudPath") <> '')))
            ON CONFLICT ("MediaItemId") DO NOTHING
            """, ct);
        var scan = await scope.ServiceProvider.GetRequiredService<WatchScanService>().RequestAsync(false, ct);
        if (scan is null || scan.RetryAt > now) return;
        await using (var start = await db.Database.BeginTransactionAsync(ct))
        {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(411060)", ct);
        if (await db.WatchScans.AnyAsync(value => value.Id == scan.Id && value.State == "cancelled", ct)) return;
        scan.State = "running";
        scan.StartedAt ??= now;
        scan.Detail = "Recherches en cours";
        scan.RetryAt = null;
        await db.SaveChangesAsync(ct);
        await start.CommitAsync(ct);
        }
        var items = await db.WatchScanItems.AsNoTracking().Where(value => value.WatchScanId == scan.Id && value.State == "queued")
            .OrderBy(value => value.Title).Take(100).ToListAsync(ct);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitor = MonitorCancellationAsync(scan.Id, batch, monitorStop.Token);
        DateTimeOffset? cooldownUntil = null;
        string? cooldownReason = null;
        var concurrency = WatchConcurrency.Resolve(settings.MaxParallelSearches, Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        scan.Detail = $"Recherches en cours : {concurrency} maximum en parallele{(settings.MaxParallelSearches == 0 ? " (automatique)" : "")}";
        await db.WatchScans.Where(value => value.Id == scan.Id && value.State != "cancelled")
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Detail, scan.Detail), ct);
        try
        {
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct }, async (item, token) =>
        {
            if (batch.IsCancellationRequested) return;
            var id = item.MediaItemId;
            using var itemScope = scopes.CreateScope();
            var itemDb = itemScope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                var currentSettings = await itemDb.WatchSettings.AsNoTracking().SingleAsync(ct);
                if (!currentSettings.Enabled) return;
                if (!await ReleaseWatchService.WatchedMedia(itemDb).AnyAsync(value => value.Id == id, ct))
                {
                    await FinishAsync(itemDb, item.Id, "skipped", "Media desactive ou film deja en stock", ct);
                    return;
                }
                var started = DateTimeOffset.UtcNow;
                await itemDb.WatchScanItems.Where(value => value.Id == item.Id && value.FinishedAt == null).ExecuteUpdateAsync(update => update
                    .SetProperty(value => value.StartedAt, started).SetProperty(value => value.State, "searching")
                    .SetProperty(value => value.Detail, "Preparation de la recherche").SetProperty(value => value.UpdatedAt, started), ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(batch.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var result = await itemScope.ServiceProvider.GetRequiredService<ReleaseWatchService>().ScanAsync(id, timeout.Token, async phase =>
                {
                    var detail = phase == "rate-limit" ? $"Attente du creneau C411 : minimum {MediaHub.Infrastructure.Clients.TorznabClient.RequestSpacingSeconds} secondes entre departs" :
                        phase == "request" ? "Requete envoyee : attente de la reponse C411" : "Filtrage, deduplication et sauvegarde des releases";
                    await itemDb.WatchScanItems.Where(value => value.Id == item.Id && value.FinishedAt == null).ExecuteUpdateAsync(update => update
                        .SetProperty(value => value.State, phase).SetProperty(value => value.Detail, detail)
                        .SetProperty(value => value.UpdatedAt, DateTimeOffset.UtcNow), timeout.Token);
                });
                if (result is null)
                {
                    await FinishAsync(itemDb, item.Id, "skipped", "Veille desactivee ou media exclu pendant la recherche", ct);
                    return;
                }
                await itemDb.WatchScanItems.Where(value => value.Id == item.Id && value.FinishedAt == null).ExecuteUpdateAsync(update => update
                    .SetProperty(value => value.Observations, result.Matching).SetProperty(value => value.Found, result.Found)
                    .SetProperty(value => value.Filtered, result.Filtered).SetProperty(value => value.NewProposals, result.NewProposals), ct);
                await FinishAsync(itemDb, item.Id, "completed", result.Baseline ? "Reference initiale enregistree sans notification" : "Recherche terminee, profil et blacklist appliques", ct);
            }
            catch (OperationCanceledException) when (batch.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await itemDb.WatchScanItems.Where(value => value.Id == item.Id && value.FinishedAt == null).ExecuteUpdateAsync(update => update
                    .SetProperty(value => value.State, "queued").SetProperty(value => value.Detail, "En attente apres suspension C411")
                    .SetProperty(value => value.StartedAt, (DateTimeOffset?)null), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var throttled = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
                var retryAt = DateTimeOffset.UtcNow.AddMinutes(throttled ? 60 : 15);
                var reason = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ? "Quota C411 (HTTP 429) : suspension pendant une heure" :
                    throttled ? "Acces C411 refuse (HTTP 401/403) : verifier les identifiants" :
                    ex is OperationCanceledException ? "Delai depasse : aucune fin de recherche sous 2 minutes" :
                    ex is HttpRequestException http ? $"Erreur reseau ou reponse C411 invalide ({http.StatusCode?.ToString() ?? "connexion"})" : $"Erreur de traitement ou de sauvegarde ({ex.GetType().Name})";
                if (!throttled) reason += " ; nouvelle tentative dans 15 minutes";
                await FinishAsync(itemDb, item.Id, "failed", reason, ct);
                await itemDb.MediaWatches.Where(value => value.MediaItemId == id).ExecuteUpdateAsync(update => update
                    .SetProperty(value => value.LastError, throttled ? "Indexeur limite ou acces refuse" : "Recherche echouee")
                    .SetProperty(value => value.NextCheckAt, retryAt), ct);
                logger.LogWarning("Watch search failed for {MediaId} ({ErrorType})", id, ex.GetType().Name);
                if (throttled) { cooldownUntil = retryAt; cooldownReason = reason; await batch.CancelAsync(); }
            }
        });
        }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
        }
        await using var finish = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(411060)", ct);
        var cancelled = await db.WatchScans.AnyAsync(value => value.Id == scan.Id && value.State == "cancelled", ct);
        if (cooldownUntil is { } until)
        {
            await db.MediaWatches.Where(value => value.NextCheckAt < until)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.NextCheckAt, until), ct);
            scan.State = "paused";
            scan.RetryAt = until;
            scan.Detail = cooldownReason;
        }
        if (cancelled)
        {
            if (cooldownUntil is { } retry)
                await db.WatchScans.Where(value => value.Id == scan.Id).ExecuteUpdateAsync(update => update.SetProperty(value => value.RetryAt, retry), ct);
            await finish.CommitAsync(ct);
            return;
        }
        if (cooldownUntil is null && !await db.WatchScanItems.AnyAsync(value => value.WatchScanId == scan.Id && value.FinishedAt == null, ct))
        {
            scan.State = "completed";
            scan.FinishedAt = DateTimeOffset.UtcNow;
            scan.Detail = "Scan termine";
        }
        else if (cooldownUntil is null)
        {
            scan.RetryAt = DateTimeOffset.UtcNow.AddSeconds(15);
            scan.Detail = "Lot termine : attente de 15 secondes avant le prochain passage du planificateur";
        }
        await db.SaveChangesAsync(ct);
        await finish.CommitAsync(ct);
        var oldScans = await db.WatchScans.Where(value => value.State == "completed" || value.State == "cancelled").OrderByDescending(value => value.CreatedAt)
            .Skip(20).Select(value => value.Id).ToListAsync(ct);
        await db.WatchScans.Where(value => oldScans.Contains(value.Id)).ExecuteDeleteAsync(ct);
    }

    private static Task<int> FinishAsync(AppDbContext db, Guid itemId, string state, string detail, CancellationToken ct) =>
        db.WatchScanItems.Where(value => value.Id == itemId && value.FinishedAt == null).ExecuteUpdateAsync(update => update
            .SetProperty(value => value.State, state).SetProperty(value => value.Detail, detail)
            .SetProperty(value => value.UpdatedAt, DateTimeOffset.UtcNow)
            .SetProperty(value => value.FinishedAt, DateTimeOffset.UtcNow), ct);

    private async Task MonitorCancellationAsync(Guid scanId, CancellationTokenSource batch, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (await db.WatchScans.AnyAsync(value => value.Id == scanId && value.State == "cancelled", ct))
                {
                    await batch.CancelAsync();
                    return;
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}