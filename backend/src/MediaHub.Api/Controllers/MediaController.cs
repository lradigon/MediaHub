using MediaHub.Api.Contracts;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MediaController(AppDbContext db, LibraryService library, ITmdbClient tmdb, ITorznabClient torznab, EpisodeSyncService episodes, StorageMediaInspector storageInspector, ICloudStorage cloud, IOptions<LibraryOptions> libraryOptions, ReleaseWatchService watch) : ControllerBase
{
    /// <summary>Liste de la bibliothèque.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<MediaSummaryDto>> List(CancellationToken ct)
    {
        var mediaItems = await db.MediaItems
            .Include(media => media.Seasons)
            .ThenInclude(season => season.Episodes)
            .OrderBy(m => m.Title)
            .ToListAsync(ct);
        var cloudFiles = await db.CloudFiles
            .Where(file => file.MediaItemId != null)
            .Select(file => new { file.MediaItemId, file.Path })
            .ToListAsync(ct);
        var cloudPathsByMediaId = cloudFiles.ToLookup(file => file.MediaItemId!.Value, file => file.Path);
        var pipelineStages = await ActivePipelineStagesAsync(ct);
        var watchEnabled = await db.WatchSettings.AnyAsync(settings => settings.Enabled, ct);
        var watchedIds = watchEnabled
            ? (await ReleaseWatchService.WatchedMedia(db).Select(media => media.Id).ToListAsync(ct)).ToHashSet()
            : new HashSet<Guid>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        return mediaItems.Select(media =>
        {
            var cloudPaths = cloudPathsByMediaId[media.Id];
            var inStock = IsInStock(media, cloudPaths.Any());
            JobKind? pipelineStage = inStock ? null : pipelineStages.GetValueOrDefault(media.Id);
            var airedEpisodes = media.Type == MediaType.Movie
                ? []
                : media.Seasons.Where(season => season.SeasonNumber != 0).SelectMany(season => season.Episodes)
                    .Where(episode => episode.AirDate is not null && episode.AirDate <= today)
                    .ToList();
            var latestAiredDate = airedEpisodes
                .Select(episode => episode.AirDate)
                .Max();
            var missingAiredEpisodeCount = airedEpisodes.Count(episode => episode.Status != EpisodeStatus.Available);
            return new MediaSummaryDto(
                media.Id, media.TmdbId, media.Type, media.Title, media.Year, media.ReleaseDate?.ToString("yyyy-MM-dd"), media.PosterPath, media.BackdropPath,
                EffectiveStatus(media, pipelineStage, inStock), inStock, pipelineStage,
                latestAiredDate?.ToString("yyyy-MM-dd"), missingAiredEpisodeCount,
                StorageName(media.StoragePath) ?? CloudStorageName(cloudPaths), watchedIds.Contains(media.Id));
        }).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MediaDetailDto>> Get(Guid id, CancellationToken ct)
    {
        var m = await db.MediaItems
            .Include(x => x.Seasons).ThenInclude(s => s.Episodes)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();

        var seasons = m.Seasons
            .Where(s => s.SeasonNumber != 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => new SeasonDto(s.Id, s.SeasonNumber, s.Title,
                s.Episodes.OrderBy(e => e.EpisodeNumber)
                    .Select(e => new EpisodeDto(e.Id, e.EpisodeNumber, e.Title,
                        e.AirDate?.ToString("yyyy-MM-dd"), e.Status, e.CloudPath))
                    .ToList()))
            .ToList();

        var cloudPaths = await db.CloudFiles.Where(file => file.MediaItemId == m.Id)
            .Select(file => file.Path).ToListAsync(ct);
        var inStock = IsInStock(m, cloudPaths.Count > 0);
        var pipelineStage = inStock ? null : await ActivePipelineStageAsync(id, ct);
        return new MediaDetailDto(m.Id, m.TmdbId, m.Type, m.Title, m.OriginalTitle,
            m.Overview, m.Year, m.PosterPath, EffectiveStatus(m, pipelineStage, inStock), inStock, pipelineStage,
            StorageName(m.StoragePath) ?? CloudStorageName(cloudPaths), seasons, await watch.StatusAsync(id, ct));
    }

    /// <summary>Liste les traitements récents associés à un média.</summary>
    [HttpGet("{id:guid}/jobs")]
    public async Task<ActionResult<IReadOnlyList<JobDto>>> Jobs(Guid id, CancellationToken ct)
    {
        var exists = await db.MediaItems.AnyAsync(media => media.Id == id, ct);
        if (!exists) return NotFound();

        var jobs = await db.PipelineJobs
            .Include(job => job.MediaItem)
            .Where(job => job.MediaItemId == id)
            .OrderByDescending(job => job.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        var cloudFiles = await db.CloudFiles
            .Where(file => file.MediaItemId == id)
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

    /// <summary>Analyse les fichiers présents sur le stockage associé au média.</summary>
    [HttpGet("{id:guid}/stock")]
    public async Task<ActionResult<IReadOnlyList<StockMediaFileDto>>> Stock(Guid id, CancellationToken ct)
    {
        var media = await db.MediaItems.FindAsync([id], ct);
        if (media is null) return NotFound();

        if (!string.IsNullOrWhiteSpace(media.StoragePath))
        {
            try { StorageAvailability.EnsureAvailable(libraryOptions.Value); }
            catch (DirectoryNotFoundException ex)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
            }
        }
        if (!string.IsNullOrWhiteSpace(media.StoragePath) &&
            !System.IO.File.Exists(media.StoragePath) && !Directory.Exists(media.StoragePath))
        {
            media.StoragePath = null;
            if (media.Status != MediaStatus.Ignored)
                media.Status = MediaStatus.Wanted;
            await db.SaveChangesAsync(ct);
            return Array.Empty<StockMediaFileDto>();
        }

        try
        {
            using var inspectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            inspectionCts.CancelAfter(TimeSpan.FromSeconds(45));
            var files = await storageInspector.InspectAsync(
                media.StoragePath, inspectionCts.Token, TimeSpan.FromSeconds(30));
            if (files.Count > 0)
            {
                return files.Select(file => new StockMediaFileDto(
                    Path.GetFileName(file.Path), file.SizeBytes, file.Duration, file.Width, file.Height,
                    file.VideoCodec, file.BitDepth, file.VideoBitRate,
                    file.AudioTracks.Select(audio => new StockAudioTrackDto(
                        audio.Language, audio.Codec, audio.Channels, audio.SampleRate)).ToList(),
                    file.SubtitleTracks.Select(subtitle => new StockSubtitleTrackDto(
                        subtitle.Language, subtitle.Format, subtitle.Source, subtitle.IsDefault, subtitle.IsForced)).ToList(),
                    false)).ToList();
            }

            return Array.Empty<StockMediaFileDto>();
        }
        catch (TimeoutException ex)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout,
                string.IsNullOrWhiteSpace(ex.Message)
                    ? "L'analyse du stockage a dépassé le délai autorisé. Vérifie l'accessibilité du lecteur monté avant de réessayer."
                    : ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout,
            "L'analyse du stockage a dépassé le délai autorisé. Vérifie l'accessibilité du lecteur Z: avant de réessayer.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>Remplace l'association TMDB d'un média et actualise ses métadonnées.</summary>
    [HttpPut("{id:guid}/association")]
    public async Task<ActionResult<MediaDetailDto>> UpdateAssociation(
        Guid id, [FromBody] UpdateMediaAssociationRequest body, CancellationToken ct)
    {
        var media = await db.MediaItems
            .Include(m => m.Seasons).ThenInclude(s => s.Episodes)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (media is null) return NotFound();

        var duplicate = await db.MediaItems
            .Include(item => item.Seasons).ThenInclude(season => season.Episodes)
            .FirstOrDefaultAsync(item => item.Id != id && item.TmdbId == body.TmdbId && item.Type == body.Type, ct);
        if (duplicate is not null)
        {
            var sourceFiles = await db.CloudFiles.Where(file => file.MediaItemId == id).ToListAsync(ct);
            if (!IsInStock(media, sourceFiles.Count > 0))
                return Conflict("Ce média est déjà présent dans la bibliothèque.");
            if (!string.IsNullOrWhiteSpace(media.StoragePath) &&
                !string.IsNullOrWhiteSpace(duplicate.StoragePath) &&
                !string.Equals(media.StoragePath.TrimEnd('\\', '/'), duplicate.StoragePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return Conflict("Le média choisi possède déjà un autre dossier de stockage.");
            if (await db.PipelineJobs.AnyAsync(job => job.MediaItemId == id &&
                (job.Status == JobStatus.Queued || job.Status == JobStatus.Running || job.Status == JobStatus.WaitingForSelection), ct))
                return Conflict("Annule les traitements actifs avant de corriger cette association.");

            var targetEpisodes = duplicate.Seasons.SelectMany(season => season.Episodes.Select(episode => new
                { season.SeasonNumber, episode.EpisodeNumber, Episode = episode }))
                .ToDictionary(item => (item.SeasonNumber, item.EpisodeNumber), item => item.Episode);
            var sourceEpisodeIds = media.Seasons.SelectMany(season => season.Episodes).Select(episode => episode.Id).ToHashSet();
            foreach (var file in sourceFiles.Where(file => file.EpisodeId is { } episodeId && !sourceEpisodeIds.Contains(episodeId)))
                file.EpisodeId = null;
            foreach (var season in media.Seasons)
            {
                foreach (var episode in season.Episodes)
                {
                    targetEpisodes.TryGetValue((season.SeasonNumber, episode.EpisodeNumber), out var targetEpisode);
                    if (!string.IsNullOrWhiteSpace(episode.CloudPath) && targetEpisode is not null)
                    {
                        targetEpisode.CloudPath = episode.CloudPath;
                        targetEpisode.Status = EpisodeStatus.Available;
                    }
                    foreach (var file in sourceFiles.Where(file => file.EpisodeId == episode.Id))
                        file.EpisodeId = targetEpisode?.Id;
                    episode.CloudPath = null;
                    if (episode.Status == EpisodeStatus.Available)
                        episode.Status = episode.AirDate <= DateOnly.FromDateTime(DateTime.UtcNow.Date)
                            ? EpisodeStatus.Missing : EpisodeStatus.Unaired;
                }
            }
            foreach (var file in sourceFiles)
                file.MediaItemId = duplicate.Id;
            if (!string.IsNullOrWhiteSpace(media.StoragePath))
                duplicate.StoragePath = media.StoragePath;
            media.StoragePath = null;
            media.Status = MediaStatus.Wanted;
            duplicate.Status = MediaStatus.Available;
            await db.SaveChangesAsync(ct);
            return await Get(duplicate.Id, ct);
        }

        var info = body.Type == MediaType.Movie
            ? await tmdb.GetMovieAsync(body.TmdbId, ct)
            : await tmdb.GetTvAsync(body.TmdbId, ct);
        if (info is null) return BadRequest("Média TMDB introuvable.");

        if (media.TmdbId != info.TmdbId || (media.Type == MediaType.Movie) != (body.Type == MediaType.Movie))
        {
            var cloudFiles = await db.CloudFiles.Where(file => file.MediaItemId == id).ToListAsync(ct);
            foreach (var file in cloudFiles)
                file.EpisodeId = null;
            db.Seasons.RemoveRange(media.Seasons);
        }
        media.TmdbId = info.TmdbId;
        media.Type = body.Type;
        media.Title = info.Title;
        media.OriginalTitle = info.OriginalTitle;
        media.OriginalLanguage = info.OriginalLanguage;
        media.Overview = info.Overview;
        media.Year = info.Year;
        media.ReleaseDate = info.ReleaseDate;
        media.PosterPath = info.PosterPath;
        media.BackdropPath = info.BackdropPath;
        await db.SaveChangesAsync(ct);

        return await Get(id, ct);
    }

    /// <summary>Modifie manuellement l'état d'un média et annule les traitements actifs associés.</summary>
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<MediaDetailDto>> UpdateStatus(
        Guid id, [FromBody] UpdateMediaStatusRequest body, CancellationToken ct)
    {
        if (body.Status == MediaStatus.Processing)
            return BadRequest("L'état Processing est réservé aux traitements actifs.");
        if (body.Status == MediaStatus.Available)
            return BadRequest("L'état Dans le stock est déterminé automatiquement par le stockage.");

        var media = await db.MediaItems.FindAsync([id], ct);
        if (media is null) return NotFound();

        await db.PipelineJobs
            .Where(job => job.MediaItemId == id &&
                (job.Status == JobStatus.Queued || job.Status == JobStatus.Running || job.Status == JobStatus.WaitingForSelection))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Status, JobStatus.Cancelled), ct);

        media.Status = body.Status;
        await db.SaveChangesAsync(ct);
        return await Get(id, ct);
    }

    /// <summary>Ajoute un média depuis TMDB et lance une recherche de release.</summary>
    [HttpPost]
    public async Task<ActionResult<MediaSummaryDto>> Add([FromBody] AddMediaRequest body, CancellationToken ct)
    {
        var m = await library.AddFromTmdbAsync(body.TmdbId, body.Type, ct);
        return CreatedAtAction(nameof(Get), new { id = m.Id },
            new MediaSummaryDto(m.Id, m.TmdbId, m.Type, m.Title, m.Year, m.ReleaseDate?.ToString("yyyy-MM-dd"), m.PosterPath, m.BackdropPath,
                MediaStatus.Wanted, false, JobKind.Search, null, 0));
    }

    /// <summary>Liste les torrents proposés pour ce média, évalués contre le profil par défaut.</summary>
    [HttpGet("{id:guid}/releases")]
    public Task<ActionResult<ReleaseSearchResponseDto>> Releases(Guid id, [FromQuery] string? coverage, CancellationToken ct) =>
        LoadReleasesAsync(id, false, coverage, ct);

    [HttpPost("{id:guid}/releases/search")]
    public Task<ActionResult<ReleaseSearchResponseDto>> SearchReleases(Guid id, [FromQuery] string? coverage, CancellationToken ct) =>
        LoadReleasesAsync(id, true, coverage, ct);

    private async Task<ActionResult<ReleaseSearchResponseDto>> LoadReleasesAsync(
        Guid id, bool forceRefresh, string? coverage, CancellationToken ct)
    {
        var m = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();

        var profile = await watch.ProfileAsync(m, ct);

        var query = SearchQuery.ForMedia(m.Title, m.Year, m.Type);
        if (m.Type != MediaType.Movie && TryParseEpisodeCoverage(coverage, out var coverageSeason, out var coverageEpisode))
            query = $"{query} S{coverageSeason:D2}E{coverageEpisode:D2}";
        var latestSeasonNumber = m.Type == MediaType.Movie
            ? null
            : await db.Seasons
                .Where(season => season.MediaItemId == m.Id)
                .OrderByDescending(season => season.SeasonNumber)
                .Select(season => (int?)season.SeasonNumber)
                .FirstOrDefaultAsync(ct);
        IReadOnlyList<ReleaseCandidate> releases;
        try
        {
            releases = forceRefresh ? await torznab.SearchAsync(
                query,
                categories: SearchQuery.CategoriesForMedia(m.Type),
                tvSearch: m.Type != MediaType.Movie,
                forceRefresh: forceRefresh,
                tmdbId: m.TmdbId,
                seasonNumber: latestSeasonNumber,
                ct: ct) : await watch.SavedReleasesAsync(m, ct);
            if (forceRefresh && string.IsNullOrWhiteSpace(coverage))
            {
                await watch.SaveReleasesAsync(id, releases, ct);
                m.ReleasesCheckedAt = DateTimeOffset.UtcNow;
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests,
                "C411 limite temporairement les recherches. Patiente quelques secondes avant de réessayer.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadGateway)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "La clé d'API C411 est absente ou invalide. Configure C411_API_KEY avant de rechercher des releases.");
            }

        var evaluatedReleases = releases
            .Select(r => ReleaseParser.Evaluate(r, profile))
            .OrderByDescending(e => e.Release.PublishDate ?? DateTimeOffset.MinValue)
            .Select(e => new ReleaseDto(
                e.Release.Title, e.Release.DownloadUrl, e.Release.SizeBytes, e.Release.Seeders,
                e.Release.Resolution, e.Release.VideoCodec, e.Release.Source, e.Release.IsMulti,
                e.Accepted, e.Score, e.RejectionReasons, e.Release.PublishDate,
                ReleaseParser.DescribeCoverage(e.Release),
                e.Release.AudioCodec, e.Release.AudioChannels, e.Release.DynamicRange, e.Release.BitDepth,
                e.Release.Grabs, e.Release.IsFreeleech))
            .ToList();

            return new ReleaseSearchResponseDto(query, evaluatedReleases, m.ReleasesCheckedAt);
    }

    [HttpGet("{id:guid}/episode-numbering")]
    public async Task<ActionResult<IReadOnlyList<EpisodeNumberingRuleDto>>> GetEpisodeNumberingRules(Guid id, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(media => media.Id == id, ct)) return NotFound();
        return await db.EpisodeNumberingRules
            .Where(rule => rule.MediaItemId == id)
            .OrderBy(rule => rule.ReleaseSeasonNumber)
            .Select(rule => new EpisodeNumberingRuleDto(rule.ReleaseSeasonNumber, rule.TmdbSeasonNumber, rule.FirstTmdbEpisodeNumber))
            .ToListAsync(ct);
    }

    [HttpPut("{id:guid}/episode-numbering")]
    public async Task<ActionResult<IReadOnlyList<EpisodeNumberingRuleDto>>> UpdateEpisodeNumberingRules(
        Guid id, [FromBody] UpdateEpisodeNumberingRulesRequest body, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(media => media.Id == id, ct)) return NotFound();
        if (body.Rules.Any(rule => rule.ReleaseSeasonNumber < 1 || rule.TmdbSeasonNumber < 1 || rule.FirstTmdbEpisodeNumber < 1) ||
            body.Rules.GroupBy(rule => rule.ReleaseSeasonNumber).Any(group => group.Count() > 1))
            return BadRequest("Chaque saison de release doit être unique et utiliser des numéros positifs.");

        var existing = await db.EpisodeNumberingRules.Where(rule => rule.MediaItemId == id).ToListAsync(ct);
        db.EpisodeNumberingRules.RemoveRange(existing);
        db.EpisodeNumberingRules.AddRange(body.Rules.Select(rule => new EpisodeNumberingRule
        {
            MediaItemId = id,
            ReleaseSeasonNumber = rule.ReleaseSeasonNumber,
            TmdbSeasonNumber = rule.TmdbSeasonNumber,
            FirstTmdbEpisodeNumber = rule.FirstTmdbEpisodeNumber
        }));
        await db.SaveChangesAsync(ct);
        return body.Rules.OrderBy(rule => rule.ReleaseSeasonNumber).ToList();
    }

    /// <summary>Lance le téléchargement d'un torrent choisi manuellement (crée un job Download).</summary>
    [HttpPost("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, [FromBody] DownloadReleaseBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.DownloadUrl))
            return BadRequest("URL de téléchargement manquante.");

        var m = await db.MediaItems.FindAsync([id], ct);
        if (m is null) return NotFound();

        var parsedRelease = new ReleaseCandidate { Title = body.Title ?? string.Empty };
        ReleaseParser.Parse(parsedRelease);
        var numberingRule = parsedRelease.SeasonNumber is { } releaseSeason
            ? await db.EpisodeNumberingRules.FirstOrDefaultAsync(rule =>
                rule.MediaItemId == m.Id && rule.ReleaseSeasonNumber == releaseSeason, ct)
            : null;

        Guid? episodeId = null;
        if (m.Type != MediaType.Movie && TryParseEpisodeCoverage(body.Coverage, out var seasonNumber, out var episodeNumber))
        {
                episodeId = await FindEpisodeIdAsync(m.Id, seasonNumber, episodeNumber, ct);
                if (episodeId is null)
                {
                    await episodes.SyncAsync(m.Id, ct);
                    episodeId = await FindEpisodeIdAsync(m.Id, seasonNumber, episodeNumber, ct);
                }
            if (episodeId is null)
                return BadRequest($"L'épisode S{seasonNumber:D2}E{episodeNumber:D2} est introuvable pour ce média.");
        }

        m.Status = MediaStatus.Processing;
        db.PipelineJobs.Add(new PipelineJob
        {
            Kind = JobKind.Download,
            MediaItemId = m.Id,
            EpisodeId = episodeId,
            // Correspond à la forme de ReleasePayload consommée par le Worker.
            PayloadJson = JsonSerializer.Serialize(new
            {
                body.DownloadUrl,
                body.Title,
                ReleaseSeasonNumber = numberingRule?.ReleaseSeasonNumber,
                TmdbSeasonNumber = numberingRule?.TmdbSeasonNumber,
                FirstTmdbEpisodeNumber = numberingRule?.FirstTmdbEpisodeNumber
            })
        });
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    private static bool TryParseEpisodeCoverage(string? coverage, out int seasonNumber, out int episodeNumber)
    {
        var match = Regex.Match(coverage ?? string.Empty, "^S(?<season>\\d{2})E(?<episode>\\d{2})$", RegexOptions.CultureInvariant);
        seasonNumber = match.Success ? int.Parse(match.Groups["season"].Value) : 0;
        episodeNumber = match.Success ? int.Parse(match.Groups["episode"].Value) : 0;
        return match.Success;
    }

        private Task<Guid?> FindEpisodeIdAsync(Guid mediaId, int seasonNumber, int episodeNumber, CancellationToken ct) =>
            db.Episodes
                .Where(episode => episode.Season!.MediaItemId == mediaId &&
                    episode.Season.SeasonNumber == seasonNumber &&
                    episode.EpisodeNumber == episodeNumber)
                .Select(episode => (Guid?)episode.Id)
                .SingleOrDefaultAsync(ct);

    /// <summary>Annule les étapes actives d'un média et restaure son statut selon le stockage détecté.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelProcessing(Guid id, CancellationToken ct)
    {
        var media = await db.MediaItems.FindAsync([id], ct);
        if (media is null) return NotFound();

        var activeJobs = await db.PipelineJobs
            .Where(job => job.MediaItemId == id &&
                (job.Status == JobStatus.Queued || job.Status == JobStatus.Running || job.Status == JobStatus.WaitingForSelection))
            .ToListAsync(ct);

        foreach (var job in activeJobs)
            job.Status = JobStatus.Cancelled;

        media.Status = string.IsNullOrWhiteSpace(media.StoragePath)
            ? MediaStatus.Wanted
            : MediaStatus.Available;
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    /// <summary>Lance un scan du stockage existant pour peupler la bibliothèque (job de fond).</summary>
    [HttpPost("scan")]
    public async Task<IActionResult> Scan(CancellationToken ct)
    {
        var alreadyRunning = await db.PipelineJobs.AnyAsync(
            j => j.Kind == JobKind.Scan && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        if (alreadyRunning)
            return Conflict("Un scan est déjà en cours.");

        db.PipelineJobs.Add(new PipelineJob { Kind = JobKind.Scan });
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    /// <summary>Actualise les métadonnées localisées de toute la bibliothèque depuis TMDB.</summary>
    [HttpPost("metadata/refresh")]
    public async Task<IActionResult> RefreshMetadata(CancellationToken ct)
    {
        var alreadyRunning = await db.PipelineJobs.AnyAsync(
            j => j.Kind == JobKind.MetadataRefresh && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), ct);
        if (alreadyRunning)
            return Conflict("Une actualisation des métadonnées est déjà en cours.");

        db.PipelineJobs.Add(new PipelineJob { Kind = JobKind.MetadataRefresh });
        await db.SaveChangesAsync(ct);
        return Accepted();
    }

    /// <summary>Synchronise les épisodes d'une série : présents sur le disque vs liste TMDB.</summary>
    [HttpPost("{id:guid}/episodes/sync")]
    public async Task<ActionResult<EpisodeSyncSummary>> SyncEpisodes(Guid id, CancellationToken ct)
    {
        var m = await db.MediaItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();
        if (m.Type == MediaType.Movie)
            return BadRequest("La synchronisation d'épisodes ne concerne que les séries et animés.");

        var summary = await episodes.SyncAsync(id, ct);
        return summary;
    }

    /// <summary>Rattache les fichiers MEGA d'un dossier aux épisodes de ce média.</summary>
    [HttpPost("{id:guid}/cloud/sync")]
    public async Task<ActionResult<CloudFolderSyncSummary>> SyncCloudFolder(
        Guid id, [FromBody] SyncCloudFolderRequest body, CancellationToken ct)
    {
        var folder = body.Folder.Trim('/');
        if (string.IsNullOrWhiteSpace(folder)) return BadRequest("Le dossier cloud est obligatoire.");

        var media = await db.MediaItems
            .Include(item => item.Seasons).ThenInclude(season => season.Episodes)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (media is null) return NotFound();
        if (media.Type == MediaType.Movie)
            return BadRequest("La synchronisation cloud ne concerne que les séries et animés.");

        var episodeIds = media.Seasons
            .SelectMany(season => season.Episodes.Select(episode => new
            {
                Season = season.SeasonNumber,
                Episode = episode.EpisodeNumber,
                episode.Id
            }))
            .GroupBy(item => (item.Season, item.Episode))
            .Select(group => group.First())
            .ToDictionary(item => (item.Season, item.Episode), item => item.Id);
        var cloudFiles = await db.CloudFiles
            .Where(file => file.MediaItemId == id)
            .ToListAsync(ct);
        var entries = await cloud.ListAsync(folder, ct);

        // Rebuild the cloud associations so files removed remotely do not stay linked.
        foreach (var episode in media.Seasons.SelectMany(season => season.Episodes))
            episode.CloudPath = null;
        db.CloudFiles.RemoveRange(cloudFiles);

        var linked = 0;

        foreach (var entry in entries.Where(entry => IsVideoFile(entry.Path)))
        {
            var match = Regex.Match(entry.Path, @"\bS(?<season>\d{1,2})\s*E(?<first>\d{1,4})(?:\s*-\s*(?<last>\d{1,4}))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int seasonNumber;
            int firstEpisode;
            int lastEpisode;
            if (!match.Success)
            {
                var seasonMatch = Regex.Match(entry.Path, @"(?:^|[/\\])S(?<season>\d{1,2})(?:\s|[/\\]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var episodeMatch = Regex.Match(Path.GetFileNameWithoutExtension(entry.Path), @"\bE(?<first>\d{1,4})(?:\s*-\s*E?(?<last>\d{1,4}))?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!episodeMatch.Success)
                    episodeMatch = Regex.Match(Path.GetFileNameWithoutExtension(entry.Path), @"(?:^|[-\s])(?<first>\d{1,4})\s+VOSTFR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!seasonMatch.Success || !episodeMatch.Success) continue;

                seasonNumber = int.Parse(seasonMatch.Groups["season"].Value);
                firstEpisode = int.Parse(episodeMatch.Groups["first"].Value);
                lastEpisode = episodeMatch.Groups["last"].Success
                    ? int.Parse(episodeMatch.Groups["last"].Value)
                    : firstEpisode;
            }
            else
            {
                seasonNumber = int.Parse(match.Groups["season"].Value);
                firstEpisode = int.Parse(match.Groups["first"].Value);
                lastEpisode = match.Groups["last"].Success
                    ? int.Parse(match.Groups["last"].Value)
                    : firstEpisode;
            }

            for (var episodeNumber = firstEpisode; episodeNumber <= lastEpisode; episodeNumber++)
            {
                if (!episodeIds.TryGetValue((seasonNumber, episodeNumber), out var episodeId)) continue;

                var episode = media.Seasons.SelectMany(season => season.Episodes).Single(item => item.Id == episodeId);
                episode.CloudPath = entry.Path;
                episode.Status = EpisodeStatus.Available;
                db.CloudFiles.Add(new CloudFile
                {
                    MediaItemId = id,
                    EpisodeId = episodeId,
                    Path = entry.Path,
                    SizeBytes = entry.SizeBytes
                });
                linked++;
            }
        }

        await episodes.SyncAsync(id, ct);
        await db.SaveChangesAsync(ct);
        return new CloudFolderSyncSummary(entries.Count, linked);
    }

    private static bool IsVideoFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".webm";

    private static string? StorageName(string? storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) return null;
        return storagePath.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault();
    }

    private static string? CloudStorageName(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var category = Array.FindIndex(parts, part => part is "Séries" or "Animés" or "Dessin Animés" or "Films" or "Reportages");
            if (category >= 0 && category + 1 < parts.Length)
                return parts[category + 1];
        }
        return null;
    }

    private async Task<Dictionary<Guid, JobKind>> ActivePipelineStagesAsync(CancellationToken ct) =>
        await db.PipelineJobs
            .Where(job => job.MediaItemId != null &&
                (job.Status == JobStatus.Queued || job.Status == JobStatus.Running || job.Status == JobStatus.WaitingForSelection))
            .OrderByDescending(job => job.Status == JobStatus.Running)
            .ThenBy(job => job.CreatedAt)
            .GroupBy(job => job.MediaItemId!.Value)
            .Select(group => new { MediaId = group.Key, Stage = group.First().Kind })
            .ToDictionaryAsync(item => item.MediaId, item => item.Stage, ct);

    private async Task<JobKind?> ActivePipelineStageAsync(Guid mediaId, CancellationToken ct) =>
        await db.PipelineJobs
            .Where(job => job.MediaItemId == mediaId &&
                (job.Status == JobStatus.Queued || job.Status == JobStatus.Running || job.Status == JobStatus.WaitingForSelection))
            .OrderByDescending(job => job.Status == JobStatus.Running)
            .ThenBy(job => job.CreatedAt)
            .Select(job => (JobKind?)job.Kind)
            .FirstOrDefaultAsync(ct);

    private static MediaStatus EffectiveStatus(MediaItem media, JobKind? pipelineStage, bool inStock) =>
        inStock ? MediaStatus.Available :
        pipelineStage is not null ? MediaStatus.Processing :
        media.Status == MediaStatus.Ignored ? MediaStatus.Ignored : MediaStatus.Wanted;

    private static bool IsInStock(MediaItem media, bool hasCloudFiles = false) =>
        !string.IsNullOrWhiteSpace(media.StoragePath) ||
        hasCloudFiles ||
        media.Seasons.SelectMany(season => season.Episodes).Any(episode => !string.IsNullOrWhiteSpace(episode.CloudPath));

    private static string? FindCloudPath(PipelineJob job, IReadOnlyList<CloudFile> cloudFiles) =>
        job.Kind == JobKind.Upload && job.Status == JobStatus.Completed
            ? cloudFiles
                .Where(file => file.EpisodeId == job.EpisodeId)
                .OrderByDescending(file => file.LastSeenAt)
                .Select(file => file.Path)
                .FirstOrDefault()
            : null;

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var m = await db.MediaItems
            .Include(media => media.Seasons).ThenInclude(season => season.Episodes)
            .FirstOrDefaultAsync(media => media.Id == id, ct);
        if (m is null) return NotFound();

        var inCloud = await db.CloudFiles.AnyAsync(file => file.MediaItemId == id, ct);
        if (IsInStock(m, inCloud))
            return Conflict("Impossible de supprimer un média présent dans le stockage.");
        if (await db.PipelineJobs.AnyAsync(job => job.MediaItemId == id && job.Status == JobStatus.Running, ct))
            return Conflict("Annule les traitements en cours avant de supprimer ce média de la wishlist.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.MediaRequests.Where(request => request.MediaItemId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.MediaItemId, (Guid?)null), ct);
        await db.PipelineJobs
            .Where(job => job.MediaItemId == id)
            .ExecuteDeleteAsync(ct);
        db.MediaItems.Remove(m);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return NoContent();
    }
}
