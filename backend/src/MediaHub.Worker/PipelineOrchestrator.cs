using System.Text.Json;
using System.Text.RegularExpressions;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using MediaHub.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MediaHub.Worker;

/// <summary>
/// Exécute une étape de pipeline et met en file la suivante.
/// Chaîne : Search → Download → Encode → Upload → Notify.
/// Les étapes dépendant de services externes (qBittorrent, HandBrake, MEGA)
/// sont fonctionnelles mais volontairement minimales : à durcir (watch de fin
/// de téléchargement, reprise sur erreur, nommage TMDB des épisodes…).
/// </summary>
public partial class PipelineOrchestrator(
    AppDbContext db,
    ITorznabClient torznab,
    ITmdbClient tmdb,
    IDownloadClient download,
    IVideoEncoder encoder,
    ICloudStorage cloud,
    StorageScanService scanner,
    LibraryService library,
    StorageMediaInspector storageInspector,
    EpisodeSyncService episodeSync,
    IServiceScopeFactory scopeFactory,
    IOptions<QBittorrentOptions> qbit,
    IOptions<HandBrakeOptions> handbrake,
    ILogger<PipelineOrchestrator> logger)
{
    private static readonly SemaphoreSlim HandBrakeGate = new(1, 1);

    [GeneratedRegex(@"\bS(?<season>\d{1,2})E(?<episode>\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodeFileRegex();

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".webm"
    };

    public async Task ProcessAsync(PipelineJob job, CancellationToken ct)
    {
        if (job.Status == JobStatus.Queued)
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.Attempts++;
            await db.SaveChangesAsync(ct);
        }
        else if (job.Status != JobStatus.Running)
        {
            return;
        }

        try
        {
            await (job.Kind switch
            {
                JobKind.Search => HandleSearchAsync(job, ct),
                JobKind.Download => HandleDownloadAsync(job, ct),
                JobKind.TrackSelection => HandleTrackSelectionAsync(job, ct),
                JobKind.Encode => HandleEncodeAsync(job, ct),
                JobKind.Upload => HandleUploadAsync(job, ct),
                JobKind.Notify => HandleNotifyAsync(job, ct),
                JobKind.Scan => HandleScanAsync(job, ct),
                JobKind.MetadataRefresh => HandleMetadataRefreshAsync(job, ct),
                _ => Task.CompletedTask
            });

            // Recharge l'état car l'API peut avoir annulé le job pendant son exécution.
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.Completed;
                job.Progress = 100;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.Status == JobStatus.Running)
            {
                job.Status = JobStatus.Queued;
                job.StartedAt = null;
                job.Error = null;
                foreach (var entry in db.ChangeTracker.Entries<PipelineJob>().Where(entry => entry.State == EntityState.Added).ToList())
                    entry.State = EntityState.Detached;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} ({Kind}) échoué", job.Id, job.Kind);
            await db.Entry(job).ReloadAsync(CancellationToken.None);
            if (job.Status != JobStatus.Cancelled)
            {
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }
        }
        finally
        {
            if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
                job.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task HandleSearchAsync(PipelineJob job, CancellationToken ct)
    {
        var media = await RequireMediaAsync(job, ct);
        var profile = await db.ReleaseProfiles.FirstOrDefaultAsync(p => p.IsDefault, ct)
            ?? new ReleaseProfile();

        var query = SearchQuery.ForMedia(media.Title, media.Year, media.Type);
        var releases = await torznab.SearchAsync(
            query,
            categories: SearchQuery.CategoriesForMedia(media.Type),
            tvSearch: media.Type != MediaType.Movie,
            tmdbId: media.TmdbId,
            ct: ct);

        var best = releases
            .Select(r => ReleaseParser.Evaluate(r, profile))
            .Where(e => e.Accepted)
            .OrderByDescending(e => e.Score)
            .FirstOrDefault();

        if (best is null)
        {
            media.Status = MediaStatus.Wanted;
            throw new InvalidOperationException($"Aucune release valide pour « {media.Title} ».");
        }

        media.Status = MediaStatus.Wanted;
        logger.LogInformation("Release valide disponible pour {Title}: {Release}", media.Title, best.Release.Title);
    }

    private async Task HandleDownloadAsync(PipelineJob job, CancellationToken ct)
    {
        var payload = ReadPayload(job);
        if (string.IsNullOrEmpty(payload.DownloadUrl))
            throw new InvalidOperationException("URL de téléchargement manquante.");

        var hash = payload.TorrentHash;
        if (string.IsNullOrWhiteSpace(hash) && !string.IsNullOrWhiteSpace(payload.Title))
            hash = (await download.FindByNameAsync(payload.Title, ct))?.Hash;

        if (string.IsNullOrWhiteSpace(hash))
        {
            hash = await download.AddTorrentAsync(payload.DownloadUrl, qbit.Value.DownloadPath, ct);
            payload = payload with { TorrentHash = hash };
            job.PayloadJson = JsonSerializer.Serialize(payload);
            await db.SaveChangesAsync(ct);
        }

        // Suit le téléchargement jusqu'à complétion : met à jour progression,
        // seeds et vitesse pour affichage temps réel, et gère l'annulation.
        var contentPath = string.IsNullOrWhiteSpace(qbit.Value.DownloadPath)
            ? payload.Title ?? hash
            : Path.Combine(qbit.Value.DownloadPath, payload.Title ?? hash);
        var pollDelay = TimeSpan.FromSeconds(3);

        while (!ct.IsCancellationRequested)
        {
            // Rafraîchit l'état du job depuis la base : détecte une annulation demandée via l'API.
            await db.Entry(job).ReloadAsync(ct);
            if (job.Status == JobStatus.Cancelled)
            {
                await TryRemoveTorrentAsync(hash, deleteFiles: true, "après annulation", ct);
                logger.LogInformation("Téléchargement annulé pour le job {JobId}", job.Id);
                return;
            }

            var status = await download.GetStatusAsync(hash, ct);
            if (status is not null)
            {
                job.Progress = Math.Clamp(status.Progress * 100, 0, 100);
                job.Seeders = status.Seeders;
                job.SizeBytes = status.SizeBytes;
                job.DownloadSpeed = status.DownloadSpeed;
                await db.SaveChangesAsync(ct);

                if (status.IsComplete)
                {
                    if (!string.IsNullOrEmpty(status.ContentPath))
                        contentPath = status.ContentPath;
                    break;
                }
            }

            await Task.Delay(pollDelay, ct);
        }

        ct.ThrowIfCancellationRequested();

        job.DownloadSpeed = 0;
        await TryRemoveTorrentAsync(hash, deleteFiles: false, "après téléchargement", ct);
        if (job.MediaItemId is { } mediaId)
        {
            await episodeSync.SyncAsync(mediaId, ct);
        }
        var episodeInputs = await FindSeasonEpisodeInputsAsync(job, contentPath, ct);
        if (episodeInputs is not null)
        {
            foreach (var episodeInput in episodeInputs)
            {
                Enqueue(JobKind.TrackSelection, job.MediaItemId, episodeInput.EpisodeId,
                    payload with
                    {
                        Title = Path.GetFileNameWithoutExtension(episodeInput.Path),
                        ContentPath = episodeInput.Path,
                        TorrentHash = hash
                    });
            }
            logger.LogInformation("Téléchargement de saison réparti en {EpisodeCount} pipelines d'épisodes manquants pour le job {JobId}.", episodeInputs.Count, job.Id);
            return;
        }

        Enqueue(JobKind.TrackSelection, job.MediaItemId, job.EpisodeId,
            payload with { ContentPath = contentPath, TorrentHash = hash });
    }

    private async Task<IReadOnlyList<(Guid EpisodeId, string Path)>?> FindSeasonEpisodeInputsAsync(
        PipelineJob job, string contentPath, CancellationToken ct)
    {
        if (job.MediaItemId is not { } mediaId || job.EpisodeId is not null || !Directory.Exists(contentPath))
            return null;

        var files = Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Select(path => (Path: path, Match: EpisodeFileRegex().Match(Path.GetFileNameWithoutExtension(path))))
            .Where(candidate => candidate.Match.Success)
            .ToList();
        if (files.Count == 0) return null;

        var mediaEpisodes = await db.Episodes
            .Where(episode => episode.Season!.MediaItemId == mediaId)
            .Select(episode => new
            {
                episode.Id,
                SeasonNumber = episode.Season!.SeasonNumber,
                episode.EpisodeNumber,
                episode.Status
            })
            .ToListAsync(ct);
        var hasNumberingRule = payloadNumbering(job, out var releaseSeason, out var tmdbSeason, out var firstTmdbEpisode);
        var episodeByKey = mediaEpisodes
            .ToDictionary(episode => (episode.SeasonNumber, episode.EpisodeNumber));
        var inputs = files
            .Select(candidate =>
            {
                var sourceKey = (
                    Season: int.Parse(candidate.Match.Groups["season"].Value),
                    Episode: int.Parse(candidate.Match.Groups["episode"].Value));
                var targetKey = MapEpisode(sourceKey.Season, sourceKey.Episode, hasNumberingRule, releaseSeason, tmdbSeason, firstTmdbEpisode);
                return (candidate.Path, TargetKey: targetKey);
            })
            .Where(candidate => episodeByKey.ContainsKey(candidate.TargetKey))
            .ToList();

        foreach (var input in inputs.Where(input =>
                     episodeByKey[input.TargetKey].Status is EpisodeStatus.Available or EpisodeStatus.Processing))
            TryDeleteSourceFile(input.Path);

        return inputs
            .Where(input => episodeByKey[input.TargetKey].Status is not (EpisodeStatus.Available or EpisodeStatus.Processing))
            .Select(input => (EpisodeId: episodeByKey[input.TargetKey].Id, Path: input.Path))
            .ToList();
    }

    private static bool payloadNumbering(PipelineJob job, out int releaseSeason, out int tmdbSeason, out int firstTmdbEpisode)
    {
        var payload = ReadPayload(job);
        releaseSeason = payload.ReleaseSeasonNumber ?? 0;
        tmdbSeason = payload.TmdbSeasonNumber ?? 0;
        firstTmdbEpisode = payload.FirstTmdbEpisodeNumber ?? 0;
        return releaseSeason > 0 && tmdbSeason > 0 && firstTmdbEpisode > 0;
    }

    private static (int Season, int Episode) MapEpisode(
        int releaseSeason, int releaseEpisode, bool hasRule, int ruleReleaseSeason, int ruleTmdbSeason, int firstTmdbEpisode) =>
        hasRule && releaseSeason == ruleReleaseSeason
            ? (ruleTmdbSeason, firstTmdbEpisode + releaseEpisode - 1)
            : (releaseSeason, releaseEpisode);

    private async Task TryRemoveTorrentAsync(string hash, bool deleteFiles, string context, CancellationToken ct)
    {
        try
        {
            await download.RemoveAsync(hash, deleteFiles, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la suppression du torrent {Hash} {Context}", hash, context);
        }
    }

    private async Task HandleEncodeAsync(PipelineJob job, CancellationToken ct)
    {
        var payload = ReadPayload(job);
        var media = await RequireMediaAsync(job, ct);
        var opt = handbrake.Value;

        var handBrakeReserved = false;
        try
        {
            await HandBrakeGate.WaitAsync(ct);
            handBrakeReserved = true;

            var preset = media.Type == MediaType.Anime ? opt.AnimePreset : opt.MoviePreset;
            var output = Path.Combine(opt.WorkingDirectory, $"{job.Id:N}-{CleanPathSegment(payload.Title, "video")}.mkv");
            var input = ResolveVideoInput(payload.ContentPath);
            Directory.CreateDirectory(opt.WorkingDirectory);

            var latestProgress = 0d;
            double? latestEncodingFps = null;
            var progress = new Progress<EncodeProgress>(value =>
            {
                latestProgress = Math.Clamp(value.Percentage, 0, 100);
                if (value.FramesPerSecond is not null) latestEncodingFps = value.FramesPerSecond;
            });
            using var encodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var monitoring = MonitorEncodingAsync(job.Id, () => (latestProgress, latestEncodingFps), encodeCts, monitorCts.Token);

            EncodeResult result;
            try
            {
                result = await encoder.EncodeAsync(
                    new EncodeRequest(input, output, opt.PresetFilePath, preset, payload.AudioTrackIndexes, payload.SubtitleTrackIndexes),
                    progress, encodeCts.Token);
            }
            finally
            {
                monitorCts.Cancel();
                try
                {
                    await monitoring;
                }
                catch (OperationCanceledException) when (monitorCts.IsCancellationRequested) { }
            }

            if (!result.Success)
                throw new InvalidOperationException($"Encodage échoué: {result.Error}");

            Enqueue(JobKind.Upload, job.MediaItemId, job.EpisodeId, payload with { OutputPath = result.OutputPath });
        }
        finally
        {
            if (handBrakeReserved) HandBrakeGate.Release();
        }
    }

    private void TryDeleteSourceFile(string path)
    {
        var downloadRoot = qbit.Value.DownloadPath;
        if (string.IsNullOrWhiteSpace(downloadRoot) ||
            !Path.GetFullPath(path).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot)) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            logger.LogWarning("Nettoyage ignoré : fichier situé hors du dossier de téléchargement configuré.");
            return;
        }
        try
        {
            File.Delete(path);
            logger.LogInformation("Fichier source supprimé après encodage : {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Échec de la suppression du fichier source après encodage : {Path}", path);
        }
    }

    private async Task HandleUploadAsync(PipelineJob job, CancellationToken ct)
    {
        var payload = ReadPayload(job);
        var media = await RequireMediaAsync(job, ct);
        if (media.Type != MediaType.Movie && job.EpisodeId is null)
        {
            job.EpisodeId = await ResolveEpisodeIdFromTitleAsync(media.Id, payload.Title, ct)
                ?? throw new InvalidOperationException("Un épisode est requis pour l'upload d'une série ou d'un animé.");
            await db.SaveChangesAsync(ct);
        }
        var (folder, fileName) = await BuildUploadDestinationAsync(media, job.EpisodeId, ct);
        var outputPath = payload.OutputPath ?? throw new InvalidOperationException("Fichier converti introuvable pour l'upload.");
        var progress = new Progress<double>(value =>
        {
            _ = UpdateUploadProgressAsync(job.Id, value);
        });
        var outputFile = new FileInfo(outputPath);
        string remotePath;
        long outputSize;
        if (outputFile.Exists)
        {
            outputSize = outputFile.Length;
            remotePath = await cloud.UploadAsync(outputPath, folder, fileName, progress, ct);
        }
        else
        {
            var uploadedFile = (await cloud.ListAsync(folder, ct))
                .FirstOrDefault(file => file.Path.EndsWith($"/{fileName}", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("Le fichier converti est introuvable et aucune copie MEGA existante n'a été trouvée.", outputPath);
            remotePath = uploadedFile.Path;
            outputSize = uploadedFile.SizeBytes;
            ((IProgress<double>)progress).Report(100);
        }
        media.Status = MediaStatus.Available;
        var cloudFile = await db.CloudFiles.FirstOrDefaultAsync(file =>
            file.MediaItemId == job.MediaItemId && file.EpisodeId == job.EpisodeId && file.Path == remotePath, ct);
        if (cloudFile is null)
        {
            db.CloudFiles.Add(new CloudFile
            {
                MediaItemId = job.MediaItemId,
                EpisodeId = job.EpisodeId,
                Path = remotePath,
                SizeBytes = outputSize
            });
        }
        else
        {
            cloudFile.LastSeenAt = DateTimeOffset.UtcNow;
            cloudFile.SizeBytes = outputSize;
        }
        if (job.EpisodeId is { } episodeId)
        {
            var ep = await db.Episodes.FindAsync([episodeId], ct);
            if (ep is not null)
            {
                ep.Status = EpisodeStatus.Available;
                ep.CloudPath = remotePath;
            }
        }

        Enqueue(JobKind.Notify, job.MediaItemId, job.EpisodeId, payload);
        await db.SaveChangesAsync(ct);
        TryDeleteConvertedFile(outputPath);
        if (!string.IsNullOrWhiteSpace(payload.ContentPath) && File.Exists(payload.ContentPath))
            TryDeleteSourceFile(payload.ContentPath);
    }

    private async Task UpdateUploadProgressAsync(Guid jobId, double value)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var progressDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await progressDb.PipelineJobs
                .Where(item => item.Id == jobId && item.Status == JobStatus.Running)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Progress, Math.Clamp(value, 0, 100)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Échec de la mise à jour de progression MEGA pour le job {JobId}", jobId);
        }
    }

    private async Task<Guid?> ResolveEpisodeIdFromTitleAsync(Guid mediaId, string? title, CancellationToken ct)
    {
        var match = Regex.Match(title ?? string.Empty, @"\bS(?<season>\d{1,2})E(?<episode>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        var seasonNumber = int.Parse(match.Groups["season"].Value);
        var episodeNumber = int.Parse(match.Groups["episode"].Value);
        return await db.Episodes
            .Where(episode => episode.Season!.MediaItemId == mediaId &&
                episode.Season.SeasonNumber == seasonNumber &&
                episode.EpisodeNumber == episodeNumber)
            .Select(episode => (Guid?)episode.Id)
            .SingleOrDefaultAsync(ct);
    }

    private void TryDeleteConvertedFile(string outputPath)
    {
        try
        {
            File.Delete(outputPath);
            logger.LogInformation("Fichier converti temporaire supprimé après upload cloud : {Path}", outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Échec de la suppression du fichier converti après upload cloud : {Path}", outputPath);
        }
    }

    private async Task<(string Folder, string FileName)> BuildUploadDestinationAsync(
        MediaItem media, Guid? episodeId, CancellationToken ct)
    {
        var title = CleanPathSegment(media.Title, "Média sans titre");
        var year = media.Year?.ToString() ?? "Année inconnue";

        if (media.Type == MediaType.Movie)
        {
            var director = media.TmdbId is { } tmdbId
                ? await tmdb.GetMovieDirectorAsync(tmdbId, ct)
                : null;
            var directorName = CleanPathSegment(director, "Réalisateur inconnu");
            return ("Films", $"{title} - {year} - {directorName}.mkv");
        }

        if (episodeId is not { } id)
            throw new InvalidOperationException("Un épisode est requis pour l'upload d'une série ou d'un animé.");

        var episode = await db.Episodes
            .Include(item => item.Season)
            .SingleOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new InvalidOperationException("Épisode introuvable pour l'upload.");
        var season = episode.Season
            ?? throw new InvalidOperationException("Saison introuvable pour l'upload.");
        var category = media.Type == MediaType.Anime ? "Animés" : "Séries";
        var episodeTitle = CleanPathSegment(episode.Title, $"Épisode {episode.EpisodeNumber}");
        var folder = $"{category}/{title} - {year}/S{season.SeasonNumber} - {title}";
        var fileName = $"S{season.SeasonNumber} E{episode.EpisodeNumber} - {episodeTitle}.mkv";
        return (folder, fileName);
    }

    private static string CleanPathSegment(string? value, string fallback)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        var cleaned = new string((value ?? string.Empty)
            .Select(character => invalidCharacters.Contains(character) ? ' ' : character)
            .ToArray());
        var normalized = string.Join(" ", cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private async Task MonitorEncodingAsync(
        Guid jobId,
        Func<(double Progress, double? EncodingFps)> getProgress,
        CancellationTokenSource encodeCts,
        CancellationToken ct)
    {
        var lastProgress = -1d;
        double? lastEncodingFps = null;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var update = getProgress();
            if (update.Progress != lastProgress || update.EncodingFps != lastEncodingFps)
            {
                await db.PipelineJobs
                    .Where(item => item.Id == jobId && item.Status == JobStatus.Running)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.Progress, update.Progress)
                        .SetProperty(item => item.EncodingFps, update.EncodingFps), ct);
                lastProgress = update.Progress;
                lastEncodingFps = update.EncodingFps;
            }

            var status = await db.PipelineJobs
                .Where(item => item.Id == jobId)
                .Select(item => item.Status)
                .SingleAsync(ct);
            if (status == JobStatus.Cancelled)
            {
                logger.LogInformation("Encodage annulé pour le job {JobId}", jobId);
                encodeCts.Cancel();
                return;
            }
        }
    }

    private Task HandleNotifyAsync(PipelineJob job, CancellationToken ct)
    {
        // TODO: déclencher un scan Plex ciblé sur le dossier concerné.
        logger.LogInformation("Média {MediaId} disponible.", job.MediaItemId);
        return Task.CompletedTask;
    }

    private async Task HandleScanAsync(PipelineJob job, CancellationToken ct)
    {
        await scanner.ScanAsync(job, ct);
    }

    private async Task HandleMetadataRefreshAsync(PipelineJob job, CancellationToken ct)
    {
        await library.RefreshMetadataAsync(job, ct);
    }

    private async Task<MediaItem> RequireMediaAsync(PipelineJob job, CancellationToken ct)
    {
        if (job.MediaItemId is not { } id)
            throw new InvalidOperationException("Job sans média associé.");
        return await db.MediaItems.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Média {id} introuvable.");
    }

    private void Enqueue(JobKind kind, Guid? mediaId, Guid? episodeId, ReleasePayload payload) =>
        db.PipelineJobs.Add(new PipelineJob
        {
            Kind = kind,
            MediaItemId = mediaId,
            EpisodeId = episodeId,
            PayloadJson = JsonSerializer.Serialize(payload)
        });

    private static ReleasePayload ReadPayload(PipelineJob job) =>
        job.PayloadJson is null
            ? new ReleasePayload(null, null, null, null, null)
            : JsonSerializer.Deserialize<ReleasePayload>(job.PayloadJson)
              ?? new ReleasePayload(null, null, null, null, null);

    private static string ResolveVideoInput(string? contentPath)
    {
        if (string.IsNullOrWhiteSpace(contentPath))
            throw new InvalidOperationException("Chemin du téléchargement manquant.");
        if (File.Exists(contentPath))
            return contentPath;
        if (!Directory.Exists(contentPath))
            throw new FileNotFoundException("Téléchargement introuvable.", contentPath);

        var video = Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".webm")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Length)
            .FirstOrDefault();

        return video?.FullName
            ?? throw new FileNotFoundException("Aucun fichier vidéo trouvé dans le téléchargement.", contentPath);
    }

    private async Task HandleTrackSelectionAsync(PipelineJob job, CancellationToken ct)
    {
        var payload = ReadPayload(job);
        var media = await RequireMediaAsync(job, ct);
        var input = ResolveVideoInput(payload.ContentPath);
        var file = (await storageInspector.InspectAsync(input, ct)).Single();
        var recommendation = TrackSelectionPolicy.Recommend(file, media.OriginalLanguage);

        job.PayloadJson = JsonSerializer.Serialize(payload with { ContentPath = input });
        if (recommendation.MissingRequirements.Count > 0)
        {
            job.Status = JobStatus.WaitingForSelection;
            job.Error = $"Choix des pistes requis : {string.Join(", ", recommendation.MissingRequirements)}.";
            await db.SaveChangesAsync(ct);
            return;
        }

        Enqueue(JobKind.Encode, job.MediaItemId, job.EpisodeId, payload with
        {
            ContentPath = input,
            AudioTrackIndexes = recommendation.AudioTrackIndexes,
            SubtitleTrackIndexes = recommendation.SubtitleTrackIndexes
        });
    }

}
