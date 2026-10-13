using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Services;

public record StockAudioTrack(int Index, string? Language, string? Codec, string? Channels, int? SampleRate, string? Name, bool IsAudioDescription)
{
    public long? BitRate { get; init; }
    public int? ChannelCount { get; init; }
    public string? Profile { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsCommentary { get; init; }
}

public record StockSubtitleTrack(int Index, string? Language, string? Format, string? Source, bool IsDefault, bool IsForced);

public record StockMediaFile(
    string Path,
    long SizeBytes,
    TimeSpan? Duration,
    int? Width,
    int? Height,
    string? VideoCodec,
    int? BitDepth,
    long? VideoBitRate,
    IReadOnlyList<StockAudioTrack> AudioTracks,
    IReadOnlyList<StockSubtitleTrack> SubtitleTracks)
{
    public string? Container { get; init; }
    public long? OverallBitRate { get; init; }
    public string? VideoProfile { get; init; }
    public string? PixelFormat { get; init; }
    public string? FrameRate { get; init; }
    public string? DisplayAspectRatio { get; init; }
    public string? ColorPrimaries { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorSpace { get; init; }
    public string? Hdr { get; init; }
    public IReadOnlyList<string> ExternalSubtitles { get; init; } = [];
}

public partial class StorageMediaInspector(IOptions<FfprobeOptions> options)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts"
    };

    private readonly FfprobeOptions _options = options.Value;

    [GeneratedRegex(@"(?:\b(?:yuv|gbr)\d{3}p(?<depth>\d{2})(?:le|be)?\b|\b(?:Main|High)\s+(?<depth>\d{2})\b)", RegexOptions.IgnoreCase)]
    private static partial Regex BitDepthRegex();

    public Task<IReadOnlyList<StockMediaFile>> ListAsync(string? storagePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            return Task.FromResult<IReadOnlyList<StockMediaFile>>(Array.Empty<StockMediaFile>());

        IEnumerable<string> files;
        if (File.Exists(storagePath))
        {
            files = [storagePath];
        }
        else if (Directory.Exists(storagePath))
        {
            files = Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)));
        }
        else
        {
            throw new FileNotFoundException("Le chemin de stockage n'est pas accessible.", storagePath);
        }

        var results = new List<StockMediaFile>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            results.Add(new StockMediaFile(
                file, info.Length, null, null, null, null, null, null,
                Array.Empty<StockAudioTrack>(), Array.Empty<StockSubtitleTrack>()));
        }
        return Task.FromResult<IReadOnlyList<StockMediaFile>>(results);
    }

    public async Task<IReadOnlyList<StockMediaFile>> InspectAsync(
        string? storagePath,
        CancellationToken ct = default,
        TimeSpan? perFileTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) return Array.Empty<StockMediaFile>();

        IEnumerable<string> files;
        if (File.Exists(storagePath))
        {
            files = [storagePath];
        }
        else if (Directory.Exists(storagePath))
        {
            files = Directory.EnumerateFiles(storagePath, "*", SearchOption.AllDirectories)
                .Where(path => VideoExtensions.Contains(Path.GetExtension(path)));
        }
        else
        {
            throw new FileNotFoundException("Le chemin de stockage n'est pas accessible.", storagePath);
        }

        var results = new List<StockMediaFile>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (perFileTimeout is { } timeout)
                fileCts.CancelAfter(timeout);
            try
            {
                results.Add(await InspectFileAsync(file, fileCts.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && fileCts.IsCancellationRequested)
            {
                throw new TimeoutException($"L'analyse de « {Path.GetFileName(file)} » a dépassé le délai autorisé.");
            }
        }
        return results;
    }

    private async Task<StockMediaFile> InspectFileAsync(string path, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(_options.CliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-probesize");
        startInfo.ArgumentList.Add("5000000");
        startInfo.ArgumentList.Add("-analyzeduration");
        startInfo.ArgumentList.Add("5000000");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Impossible de démarrer ffprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe n'a pas pu analyser « {Path.GetFileName(path)} » : {error}");

        using var document = JsonDocument.Parse(output);
        return ParseProbe(path, new FileInfo(path).Length, document.RootElement);
    }

    public static StockMediaFile ParseProbe(string path, long sizeBytes, JsonElement root)
    {
        var streams = root.TryGetProperty("streams", out var streamList)
            ? streamList.EnumerateArray().ToList()
            : [];
        var video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video" && !GetBool(GetObject(stream, "disposition"), "attached_pic"));
        var format = GetObject(root, "format");
        var audioTracks = streams.Where(stream => GetString(stream, "codec_type") == "audio")
            .Select((audio, index) =>
            {
                var tags = GetObject(audio, "tags");
                var name = GetString(tags, "title");
                return new StockAudioTrack(
                    index + 1,
                    GetString(tags, "language"),
                    GetString(audio, "codec_name"),
                    GetString(audio, "channel_layout") ?? GetInt(audio, "channels")?.ToString(),
                    GetInt(audio, "sample_rate"),
                    name,
                    IsAudioDescription(name))
                {
                    BitRate = GetLong(audio, "bit_rate") ?? GetLong(tags, "BPS") ?? GetLong(tags, "BPS-eng"),
                    ChannelCount = GetInt(audio, "channels"),
                    Profile = GetString(audio, "profile"),
                    IsDefault = GetBool(GetObject(audio, "disposition"), "default"),
                    IsForced = GetBool(GetObject(audio, "disposition"), "forced"),
                    IsCommentary = GetBool(GetObject(audio, "disposition"), "comment")
                };
            }).ToList();
        var subtitleTracks = streams.Where(stream => GetString(stream, "codec_type") == "subtitle")
            .Select((subtitle, index) =>
            {
                var tags = GetObject(subtitle, "tags");
                var disposition = GetObject(subtitle, "disposition");
                var isDefault = GetBool(disposition, "default");
                var isForced = GetBool(disposition, "forced");
                return new StockSubtitleTrack(
                    index + 1,
                    GetString(tags, "language"),
                    GetString(subtitle, "codec_name"),
                    GetSubtitleName(GetString(tags, "title"), isDefault, isForced),
                    isDefault,
                    isForced);
            }).ToList();

        return new StockMediaFile(
            path,
            sizeBytes,
            GetDuration(root),
            GetInt(video, "width"),
            GetInt(video, "height"),
            GetString(video, "codec_name"),
            GetBitDepth(GetString(video, "pix_fmt") ?? GetString(video, "profile") ?? string.Empty)
                ?? (GetString(video, "pix_fmt") is { } pixel && pixel.Contains("420p") ? 8 : null),
            GetLong(video, "bit_rate") ?? GetLong(GetObject(video, "tags"), "BPS") ?? GetLong(GetObject(video, "tags"), "BPS-eng"),
            audioTracks,
            subtitleTracks)
        {
            Container = GetString(format, "format_name"),
            OverallBitRate = GetLong(format, "bit_rate"),
            VideoProfile = GetString(video, "profile"),
            PixelFormat = GetString(video, "pix_fmt"),
            FrameRate = GetString(video, "avg_frame_rate"),
            DisplayAspectRatio = GetString(video, "display_aspect_ratio"),
            ColorPrimaries = GetString(video, "color_primaries"),
            ColorTransfer = GetString(video, "color_transfer"),
            ColorSpace = GetString(video, "color_space"),
            Hdr = GetHdr(video)
        };
    }

    private static string? GetHdr(JsonElement video)
    {
        if (video.ValueKind == JsonValueKind.Object && video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array &&
            sideData.EnumerateArray().Any(value => GetString(value, "side_data_type")?.Contains("DOVI", StringComparison.OrdinalIgnoreCase) == true)) return "Dolby Vision";
        return GetString(video, "color_transfer") switch { "smpte2084" => "PQ / HDR", "arib-std-b67" => "HLG", _ => null };
    }

    private static JsonElement GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static int? GetInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static long? GetLong(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : null;
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 1);

    private static string? GetSubtitleName(string? title, bool isDefault, bool isForced) =>
        !string.IsNullOrWhiteSpace(title) ? title :
        isForced ? "Forcés" :
        isDefault ? "Par défaut" : "Standard";

    private static bool IsAudioDescription(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("audio description", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("audiodescription", StringComparison.OrdinalIgnoreCase));

    private static int? GetBitDepth(string output)
    {
        var match = BitDepthRegex().Match(output);
        return match.Success && int.TryParse(match.Groups["depth"].Value, out var depth) ? depth : null;
    }

    private static TimeSpan? GetDuration(JsonElement root)
    {
        var format = GetObject(root, "format");
        var seconds = GetString(format, "duration");
        return double.TryParse(seconds, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var duration)
            ? TimeSpan.FromSeconds(duration)
            : null;
    }
}