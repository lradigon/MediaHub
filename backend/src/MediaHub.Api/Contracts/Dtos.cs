using MediaHub.Core;
using MediaHub.Core.Entities;

namespace MediaHub.Api.Contracts;

public record MediaSummaryDto(
    Guid Id, int? TmdbId, MediaType Type, string Title, int? Year,
    string? ReleaseDate, string? PosterPath, string? BackdropPath, MediaStatus Status, bool InStock, JobKind? PipelineStage,
    string? LatestAiredEpisodeDate, int MissingAiredEpisodeCount, string? StorageName = null);

public record EpisodeDto(Guid Id, int EpisodeNumber, string? Title, string? AirDate, EpisodeStatus Status, string? CloudPath);

public record SeasonDto(Guid Id, int SeasonNumber, string? Title, IReadOnlyList<EpisodeDto> Episodes);

public record MediaDetailDto(
    Guid Id, int? TmdbId, MediaType Type, string Title, string? OriginalTitle,
    string? Overview, int? Year, string? PosterPath, MediaStatus Status,
    bool InStock, JobKind? PipelineStage, string? StorageName, IReadOnlyList<SeasonDto> Seasons);

public record StockAudioTrackDto(string? Language, string? Codec, string? Channels, int? SampleRate);

public record StockSubtitleTrackDto(string? Language, string? Format, string? Source, bool IsDefault, bool IsForced);

public record StockMediaFileDto(
    string Name, long SizeBytes, TimeSpan? Duration, int? Width, int? Height,
    string? VideoCodec, int? BitDepth, long? VideoBitRate, IReadOnlyList<StockAudioTrackDto> AudioTracks,
    IReadOnlyList<StockSubtitleTrackDto> SubtitleTracks, bool IsCloud);

/// <summary>Corps pour ajouter un média à la bibliothèque depuis TMDB.</summary>
public record AddMediaRequest(int TmdbId, MediaType Type);

/// <summary>Corps pour corriger l'association TMDB d'un média existant.</summary>
public record UpdateMediaAssociationRequest(int TmdbId, MediaType Type);

/// <summary>Corps pour modifier manuellement l'état d'un média.</summary>
public record UpdateMediaStatusRequest(MediaStatus Status);

/// <summary>Corps pour lancer le téléchargement d'un torrent choisi manuellement.</summary>
public record DownloadReleaseBody(string DownloadUrl, string? Title, string? Coverage);

public record EpisodeNumberingRuleDto(int ReleaseSeasonNumber, int TmdbSeasonNumber, int FirstTmdbEpisodeNumber);
public record UpdateEpisodeNumberingRulesRequest(IReadOnlyList<EpisodeNumberingRuleDto> Rules);

public record CreateRequestBody(int TmdbId, MediaType Type, Guid? RequestedByUserId);

public record ReleaseDto(
    string Title, string? DownloadUrl, long SizeBytes, int Seeders,
    string? Resolution, string? VideoCodec, string? Source, bool IsMulti,
    bool Accepted, int Score, IReadOnlyList<string> RejectionReasons,
    DateTimeOffset? PublishDate, string Coverage,
    string? AudioCodec, string? AudioChannels, string? DynamicRange, int? BitDepth,
    int Grabs, bool Freeleech);

public record ReleaseSearchResponseDto(string Query, IReadOnlyList<ReleaseDto> Releases);

public record JobDto(
    Guid Id, JobKind Kind, JobStatus Status, double Progress,
    Guid? MediaItemId, string? MediaTitle, int Seeders, long SizeBytes, long DownloadSpeed,
    double? EncodingFps, string? Error, DateTimeOffset CreatedAt, long? EstimatedRemainingSeconds,
    string? CloudPath, int? SeasonNumber, int? EpisodeNumber, string? EpisodeTitle)
{
    public static JobDto From(PipelineJob job, DateTimeOffset now, string? cloudPath = null, Episode? episode = null)
    {
        long? estimatedRemainingSeconds = null;
        if (job.Status == JobStatus.Running && job.Progress is > 0 and < 100)
        {
            if (job.Kind == JobKind.Download && job.SizeBytes > 0 && job.DownloadSpeed > 0)
            {
                var remainingBytes = job.SizeBytes * (1 - job.Progress / 100d);
                estimatedRemainingSeconds = (long)Math.Ceiling(remainingBytes / job.DownloadSpeed);
            }
            else if (job.StartedAt is { } startedAt)
            {
                var elapsedSeconds = (now - startedAt).TotalSeconds;
                if (elapsedSeconds > 0)
                    estimatedRemainingSeconds = (long)Math.Ceiling(elapsedSeconds * (100 - job.Progress) / job.Progress);
            }
        }

        return new JobDto(
            job.Id, job.Kind, job.Status, job.Progress, job.MediaItemId, job.MediaItem?.Title,
            job.Seeders, job.SizeBytes, job.DownloadSpeed, job.EncodingFps, job.Error, job.CreatedAt,
            estimatedRemainingSeconds, cloudPath, episode?.Season?.SeasonNumber, episode?.EpisodeNumber, episode?.Title);
    }
}

public record JobPageDto(int Page, int PageSize, int TotalCount, IReadOnlyList<JobDto> Jobs);

public record JobAudioTrackDto(int Index, string? Language, string? Codec, string? Channels, int? SampleRate, string? Name, bool IsAudioDescription);

public record JobSubtitleTrackDto(int Index, string? Language, string? Format, string? Source, bool IsDefault, bool IsForced, bool IsSdh);

public record TrackSelectionDto(
    Guid JobId, string? MediaTitle, string? OriginalLanguage, string? Reason,
    IReadOnlyList<JobAudioTrackDto> AudioTracks, IReadOnlyList<JobSubtitleTrackDto> SubtitleTracks,
    IReadOnlyList<int> RecommendedAudioTrackIndexes, IReadOnlyList<int> RecommendedSubtitleTrackIndexes,
    IReadOnlyList<string> MissingRequirements);

public record ConfirmTrackSelectionRequest(IReadOnlyList<int> AudioTrackIndexes, IReadOnlyList<int> SubtitleTrackIndexes);

public record GroupTrackSelectionResultDto(int AppliedCount);

public record SyncCloudFolderRequest(string Folder);

public record CloudFolderSyncSummary(int FilesFound, int EpisodesLinked);

public record C411AccountStatsDto(string Username, long UploadedBytes, long DownloadedBytes, double Ratio);

/// <summary>Profil de qualité/filtre de release (éditable depuis l'écran de réglages).</summary>
public record ReleaseProfileDto(
    Guid Id, string Name, bool RequireMulti, int MinSeeders,
    List<string> AllowedResolutions, List<string> AllowedVideoCodecs,
    List<string> RejectedVideoCodecs, List<string> AllowedSources,
    List<string> RejectedTerms);
