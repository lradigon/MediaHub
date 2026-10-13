using MediaHub.Api.Contracts;
using MediaHub.Core;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController(AppDbContext db, TrackSelectionService selections) : ControllerBase
{
    /// <summary>Jobs de pipeline récents (file de traitement).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<JobDto>> List(CancellationToken ct)
    {
        var jobs = await db.PipelineJobs
            .Include(job => job.MediaItem)
            .OrderByDescending(j => j.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var mediaIds = jobs.Where(job => job.MediaItemId is not null).Select(job => job.MediaItemId!.Value).Distinct().ToList();
        var cloudFiles = await db.CloudFiles
            .Where(file => file.MediaItemId.HasValue && mediaIds.Contains(file.MediaItemId.Value))
            .ToListAsync(ct);
        var episodeIds = jobs.Where(job => job.EpisodeId.HasValue).Select(job => job.EpisodeId!.Value).Distinct().ToList();
        var episodes = await db.Episodes
            .Include(episode => episode.Season)
            .Where(episode => episodeIds.Contains(episode.Id))
            .ToDictionaryAsync(episode => episode.Id, ct);

        var now = DateTimeOffset.UtcNow;
        return jobs.Select(job => JobDto.From(
            job, now, FindCloudPath(job, cloudFiles),
            job.EpisodeId is { } episodeId ? episodes.GetValueOrDefault(episodeId) : null)).ToList();
    }

    /// <summary>Liste paginée de tous les jobs de pipeline.</summary>
    [HttpGet("page")]
    public async Task<JobPageDto> Page(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] JobKind? kind = null,
        [FromQuery] JobStatus? status = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 100);
        var jobsQuery = db.PipelineJobs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim()}%";
            jobsQuery = jobsQuery.Where(job => job.MediaItem != null && EF.Functions.ILike(job.MediaItem.Title, pattern));
        }
        if (kind is not null)
            jobsQuery = jobsQuery.Where(job => job.Kind == kind);
        if (status is not null)
            jobsQuery = jobsQuery.Where(job => job.Status == status);

        var totalCount = await jobsQuery.CountAsync(ct);
        var jobs = await jobsQuery
            .Include(job => job.MediaItem)
            .OrderByDescending(job => job.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var mediaIds = jobs.Where(job => job.MediaItemId is not null).Select(job => job.MediaItemId!.Value).Distinct().ToList();
        var cloudFiles = await db.CloudFiles
            .Where(file => file.MediaItemId.HasValue && mediaIds.Contains(file.MediaItemId.Value))
            .ToListAsync(ct);
        var episodeIds = jobs.Where(job => job.EpisodeId.HasValue).Select(job => job.EpisodeId!.Value).Distinct().ToList();
        var episodes = await db.Episodes
            .Include(episode => episode.Season)
            .Where(episode => episodeIds.Contains(episode.Id))
            .ToDictionaryAsync(episode => episode.Id, ct);
        var now = DateTimeOffset.UtcNow;

        return new JobPageDto(page, pageSize, totalCount, jobs.Select(job => JobDto.From(
            job, now, FindCloudPath(job, cloudFiles),
            job.EpisodeId is { } episodeId ? episodes.GetValueOrDefault(episodeId) : null)).ToList());
    }

    /// <summary>Annule un job en file ou en cours (le Worker arrêtera et supprimera le torrent).</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        // Mise à jour ciblée du seul statut, pour ne pas écraser la progression écrite par le Worker.
        var rows = await db.PipelineJobs
            .Where(j => j.Id == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running || j.Status == JobStatus.WaitingForSelection))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled), ct);

        if (rows == 0)
            return NotFound("Job introuvable ou non annulable (déjà terminé).");
        return Accepted();
    }

    /// <summary>Recrée un job annulé ou échoué à partir de sa propre étape.</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var previous = await db.PipelineJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == id, ct);
        if (previous is null) return NotFound("Job introuvable.");
        if (previous.Status is not (JobStatus.Cancelled or JobStatus.Failed))
            return Conflict("Seuls les jobs annulés ou échoués peuvent être relancés.");

        db.PipelineJobs.Add(new PipelineJob
        {
            Kind = previous.Kind,
            MediaItemId = previous.MediaItemId,
            EpisodeId = previous.EpisodeId,
            PayloadJson = previous.PayloadJson
        });
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    [HttpGet("{id:guid}/track-selection")]
    public async Task<ActionResult<TrackSelectionDto>> GetTrackSelection(Guid id, CancellationToken ct)
    {
        var job = await db.PipelineJobs.Include(job => job.MediaItem).FirstOrDefaultAsync(job => job.Id == id, ct);
        if (job is null) return NotFound();
        if (job.Kind != JobKind.TrackSelection || job.Status != JobStatus.WaitingForSelection)
            return Conflict("Ce job n'est pas en attente d'une sélection de pistes.");

        try
        {
            var details = await selections.GetAsync(id, ct);
            if (details is null) return Conflict("Ce job n'est plus en attente d'une sélection de pistes.");
            var file = details.File;
            var recommendation = details.Recommendation;
            return new TrackSelectionDto(
                job.Id, job.MediaItem?.Title, job.MediaItem?.OriginalLanguage, job.Error,
                file.AudioTracks.Select(track => new JobAudioTrackDto(
                    track.Index, track.Language, track.Codec, track.Channels, track.SampleRate, track.Name, track.IsAudioDescription)).ToList(),
                file.SubtitleTracks.Select(track => new JobSubtitleTrackDto(
                    track.Index, track.Language, track.Format, track.Source, track.IsDefault, track.IsForced, TrackSelectionPolicy.IsSdh(track))).ToList(),
                recommendation.AudioTrackIndexes, recommendation.SubtitleTrackIndexes, recommendation.MissingRequirements);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    [HttpPost("{id:guid}/track-selection")]
    public async Task<IActionResult> ConfirmTrackSelection(
        Guid id, [FromBody] ConfirmTrackSelectionRequest body, CancellationToken ct)
    {
        var job = await db.PipelineJobs.FirstOrDefaultAsync(job => job.Id == id, ct);
        if (job is null) return NotFound();
        if (job.Kind != JobKind.TrackSelection || job.Status != JobStatus.WaitingForSelection)
            return Conflict("Ce job n'est pas en attente d'une sélection de pistes.");

        try
        {
            var result = await selections.ConfirmAsync(id, body.AudioTrackIndexes, body.SubtitleTrackIndexes, false, ct);
            if (result.Error is not null) return BadRequest(result.Error);
            return Accepted();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    [HttpPost("{id:guid}/track-selection/group")]
    public async Task<ActionResult<GroupTrackSelectionResultDto>> ConfirmMatchingTrackSelections(
        Guid id, [FromBody] ConfirmTrackSelectionRequest body, CancellationToken ct)
    {
        var sourceJob = await db.PipelineJobs.FirstOrDefaultAsync(job => job.Id == id, ct);
        if (sourceJob is null) return NotFound();
        if (sourceJob.Kind != JobKind.TrackSelection || sourceJob.Status != JobStatus.WaitingForSelection)
            return Conflict("Ce job n'est pas en attente d'une sélection de pistes.");

        try
        {
            var result = await selections.ConfirmAsync(id, body.AudioTrackIndexes, body.SubtitleTrackIndexes, true, ct);
            if (result.Error is not null) return BadRequest(result.Error);
            return Ok(new GroupTrackSelectionResultDto(result.AppliedCount));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    private static string? FindCloudPath(PipelineJob job, IReadOnlyList<CloudFile> cloudFiles) =>
        job.Kind == JobKind.Upload && job.Status == JobStatus.Completed
            ? cloudFiles
                .Where(file => file.MediaItemId == job.MediaItemId && file.EpisodeId == job.EpisodeId)
                .OrderByDescending(file => file.LastSeenAt)
                .Select(file => file.Path)
                .FirstOrDefault()
            : null;

}
