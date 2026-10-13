using System.Text.Json;
using MediaHub.Core;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Infrastructure.Services;

public record TrackSelectionDetails(PipelineJob Job, ReleasePayload Payload, StockMediaFile File, TrackRecommendation Recommendation);
public record TrackConfirmationResult(int AppliedCount, string? Error = null);

public class TrackSelectionService(AppDbContext db, StorageMediaInspector inspector)
{
    public async Task<TrackSelectionDetails?> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await db.PipelineJobs.AsNoTracking().Include(value => value.MediaItem)
            .FirstOrDefaultAsync(value => value.Id == id && value.Kind == JobKind.TrackSelection && value.Status == JobStatus.WaitingForSelection, ct);
        if (job is null) return null;
        var (payload, file) = await InspectAsync(job, ct);
        return new(job, payload, file, TrackSelectionPolicy.Recommend(file, job.MediaItem?.OriginalLanguage));
    }

    public async Task<TrackConfirmationResult> ConfirmAsync(Guid id, IReadOnlyList<int> audio, IReadOnlyList<int> subtitles,
        bool matching, CancellationToken ct, string? expectedLayout = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8112301)", ct);
        var source = await GetAsync(id, ct);
        if (source is null) return new(0, "Ce job n'est plus en attente d'une selection de pistes.");
        if (expectedLayout is not null && LayoutSignature(source.File) != expectedLayout)
            return new(0, "Les pistes ont change. Actualise la demande avant de confirmer.");
        var audioIndexes = audio.Distinct().ToList();
        var subtitleIndexes = subtitles.Distinct().ToList();
        var error = Validate(source.File, audioIndexes, subtitleIndexes);
        if (error is not null) return new(0, error);
        var candidates = matching
            ? await db.PipelineJobs.AsNoTracking().Where(value => value.Kind == JobKind.TrackSelection &&
                value.Status == JobStatus.WaitingForSelection && value.MediaItemId == source.Job.MediaItemId).ToListAsync(ct)
            : [source.Job];
        var signature = LayoutSignature(source.File);
        var applied = 0;
        foreach (var job in candidates)
        {
            try
            {
                var (payload, file) = job.Id == source.Job.Id ? (source.Payload, source.File) : await InspectAsync(job, ct);
                if (LayoutSignature(file) != signature) continue;
                var now = DateTimeOffset.UtcNow;
                var changed = await db.PipelineJobs.Where(value => value.Id == job.Id && value.Status == JobStatus.WaitingForSelection)
                    .ExecuteUpdateAsync(update => update.SetProperty(value => value.Status, JobStatus.Completed)
                        .SetProperty(value => value.Progress, 100).SetProperty(value => value.Error, (string?)null)
                        .SetProperty(value => value.FinishedAt, now), ct);
                if (changed == 0) continue;
                db.PipelineJobs.Add(new PipelineJob
                {
                    Kind = JobKind.Encode, MediaItemId = job.MediaItemId, EpisodeId = job.EpisodeId,
                    PayloadJson = JsonSerializer.Serialize(payload with { AudioTrackIndexes = audioIndexes, SubtitleTrackIndexes = subtitleIndexes })
                });
                applied++;
            }
            catch (Exception exception) when (matching && exception is IOException or InvalidOperationException)
            {
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(applied, applied == 0 ? "Ce job n'est plus en attente d'une selection de pistes." : null);
    }

    public static string? Validate(StockMediaFile file, IReadOnlyList<int> audio, IReadOnlyList<int> subtitles)
    {
        if (audio.Count == 0) return "Selectionne au moins une piste audio.";
        if (audio.Any(index => file.AudioTracks.All(track => track.Index != index)) ||
            subtitles.Any(index => file.SubtitleTracks.All(track => track.Index != index)))
            return "La selection contient une piste absente du fichier telecharge.";
        if (file.AudioTracks.Any(track => track.IsAudioDescription && audio.Contains(track.Index)))
            return "Les pistes d'audiodescription ne peuvent pas etre retenues.";
        if (file.SubtitleTracks.Any(track => TrackSelectionPolicy.IsSdh(track) && subtitles.Contains(track.Index)))
            return "Les sous-titres SDH ne peuvent pas etre retenus.";
        return null;
    }

    public static string LayoutSignature(StockMediaFile file) => JsonSerializer.Serialize(new
    {
        AudioTracks = file.AudioTracks.Select(track => new { track.Index, track.Language, track.Codec, track.Channels, track.SampleRate, track.Name, track.IsAudioDescription }),
        file.SubtitleTracks
    });

    private async Task<(ReleasePayload Payload, StockMediaFile File)> InspectAsync(PipelineJob job, CancellationToken ct)
    {
        var payload = job.PayloadJson is null ? new ReleasePayload(null, null, null, null, null)
            : JsonSerializer.Deserialize<ReleasePayload>(job.PayloadJson) ?? new ReleasePayload(null, null, null, null, null);
        if (string.IsNullOrWhiteSpace(payload.ContentPath)) throw new InvalidOperationException("Le chemin du telechargement n'est pas disponible.");
        var file = (await inspector.InspectAsync(payload.ContentPath, ct)).OrderByDescending(value => value.SizeBytes).FirstOrDefault()
            ?? throw new FileNotFoundException("Aucun fichier video n'a ete trouve dans le telechargement.");
        return (payload with { ContentPath = file.Path }, file);
    }
}