using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Worker;

/// <summary>
/// Boucle de fond qui consomme la table PipelineJobs comme une file de tâches.
/// Les étapes indépendantes peuvent être traitées en parallèle. Les encodages
/// HandBrake restent exclusifs afin de ne pas saturer la machine.
/// </summary>
public class PipelineWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PipelineWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private readonly int _maxConcurrentJobs = Math.Max(1, configuration.GetValue<int?>("Pipeline:MaxConcurrentJobs") ?? 3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedJobsAsync(stoppingToken);
        logger.LogInformation("PipelineWorker démarré avec {MaxConcurrentJobs} jobs simultanés.", _maxConcurrentJobs);
        var runningJobs = new List<(Task Task, bool IsEncoding)>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var heartbeatPath = configuration["Pipeline:HeartbeatPath"];
                if (!string.IsNullOrWhiteSpace(heartbeatPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(heartbeatPath))!);
                    await File.WriteAllTextAsync($"{heartbeatPath}.tmp", DateTimeOffset.UtcNow.ToString("O"), stoppingToken);
                    File.Move($"{heartbeatPath}.tmp", heartbeatPath, overwrite: true);
                }
                runningJobs.RemoveAll(job => job.Task.IsCompleted);
                var started = false;

                while (runningJobs.Count < _maxConcurrentJobs)
                {
                    var job = await ClaimNextAsync(runningJobs.Any(job => job.IsEncoding), stoppingToken);
                    if (job is null) break;

                    runningJobs.Add((ProcessAsync(job.Id, stoppingToken), job.Kind == Core.JobKind.Encode));
                    started = true;
                }

                if (!started)
                    await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erreur inattendue dans la boucle du worker.");
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
        try
        {
            await Task.WhenAll(runningJobs.Select(job => job.Task));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requeued = await db.PipelineJobs
            .Where(job => job.Status == Core.JobStatus.Running)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, Core.JobStatus.Queued)
                .SetProperty(job => job.StartedAt, (DateTimeOffset?)null), ct);
        if (requeued > 0)
            logger.LogWarning("{JobCount} job(s) interrompu(s) remis en file après le redémarrage du worker.", requeued);
    }

    private async Task<PipelineJob?> ClaimNextAsync(bool encodingRunning, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var encodingActive = encodingRunning || await db.PipelineJobs.AnyAsync(
            job => job.Kind == Core.JobKind.Encode && job.Status == Core.JobStatus.Running, ct);
        var candidatesQuery = db.PipelineJobs
            .Where(j => j.Status == Core.JobStatus.Queued);
        if (encodingActive)
            candidatesQuery = candidatesQuery.Where(job => job.Kind != Core.JobKind.Encode);

        var candidates = await candidatesQuery
            .OrderBy(j => j.CreatedAt)
            .Take(_maxConcurrentJobs * 4)
            .ToListAsync(ct);
        foreach (var candidate in candidates.Where(candidate =>
                     candidate.Kind != Core.JobKind.Encode || !encodingActive))
        {
            var claimed = await db.PipelineJobs
                .Where(job => job.Id == candidate.Id && job.Status == Core.JobStatus.Queued)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, Core.JobStatus.Running)
                    .SetProperty(job => job.StartedAt, DateTimeOffset.UtcNow)
                    .SetProperty(job => job.Attempts, job => job.Attempts + 1), ct);
            if (claimed == 1) return candidate;
        }

        return null;
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.PipelineJobs.FindAsync([jobId], ct);
        if (job is null || job.Status != Core.JobStatus.Running) return;
        var orchestrator = scope.ServiceProvider.GetRequiredService<PipelineOrchestrator>();
        await orchestrator.ProcessAsync(job, ct);
    }
}
