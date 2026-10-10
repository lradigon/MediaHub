namespace MediaHub.Core.Abstractions;

public record EncodeRequest(
    string InputPath,
    string OutputPath,
    string PresetFilePath,
    string PresetName,
    IReadOnlyList<int>? AudioTrackIndexes = null,
    IReadOnlyList<int>? SubtitleTrackIndexes = null);

public record EncodeResult(bool Success, string OutputPath, string? Error);

public record EncodeProgress(double Percentage, double? FramesPerSecond);

/// <summary>Ré-encodage vidéo via HandBrakeCLI avec un preset donné.</summary>
public interface IVideoEncoder
{
    Task<EncodeResult> EncodeAsync(
        EncodeRequest request,
        IProgress<EncodeProgress>? progress = null,
        CancellationToken ct = default);
}
