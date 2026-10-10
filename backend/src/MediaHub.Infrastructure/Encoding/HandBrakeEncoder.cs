using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Encoding;

/// <summary>
/// Ré-encodage via HandBrakeCLI. Reprend la logique du script Python d'origine :
/// import du preset puis encodage, avec parsing de la progression en direct.
/// </summary>
public partial class HandBrakeEncoder(
    IOptions<HandBrakeOptions> options,
    ILogger<HandBrakeEncoder> logger) : IVideoEncoder
{
    private readonly HandBrakeOptions _opt = options.Value;

    [GeneratedRegex(@"Encoding.*?(?<percentage>\d{1,3}(?:[\.,]\d{1,2})?)\s*%.*?\((?<current>\d+(?:[\.,]\d+)?)\s*fps(?:,\s*avg\s*(?<average>\d+(?:[\.,]\d+)?)\s*fps)?")]
    private static partial Regex ProgressRegex();

    public async Task<EncodeResult> EncodeAsync(
        EncodeRequest request, IProgress<EncodeProgress>? progress = null, CancellationToken ct = default)
    {
        // HandBrakeCLI --preset-import-file <file> --preset <name> -i <in> -o <out>
        var psi = new ProcessStartInfo(_opt.CliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("--preset-import-file");
        psi.ArgumentList.Add(request.PresetFilePath);
        psi.ArgumentList.Add("--preset");
        psi.ArgumentList.Add(request.PresetName);
        if (request.AudioTrackIndexes is { Count: > 0 })
        {
            psi.ArgumentList.Add("--audio");
            psi.ArgumentList.Add(string.Join(',', request.AudioTrackIndexes));
            psi.ArgumentList.Add("--aencoder");
            psi.ArgumentList.Add(string.Join(',', Enumerable.Repeat("av_aac", request.AudioTrackIndexes.Count)));
            psi.ArgumentList.Add("--mixdown");
            psi.ArgumentList.Add(string.Join(',', Enumerable.Repeat("stereo", request.AudioTrackIndexes.Count)));
            psi.ArgumentList.Add("--arate");
            psi.ArgumentList.Add(string.Join(',', Enumerable.Repeat("48", request.AudioTrackIndexes.Count)));
            psi.ArgumentList.Add("--ab");
            psi.ArgumentList.Add(string.Join(',', Enumerable.Repeat("512", request.AudioTrackIndexes.Count)));
        }
        if (request.SubtitleTrackIndexes is { Count: > 0 })
        {
            psi.ArgumentList.Add("--subtitle");
            psi.ArgumentList.Add(string.Join(',', request.SubtitleTrackIndexes));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(request.InputPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(request.OutputPath);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Impossible de démarrer HandBrakeCLI");

            proc.OutputDataReceived += (_, e) => HandleLine(e.Data, progress);
            proc.ErrorDataReceived += (_, e) => HandleLine(e.Data, progress);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);

            return proc.ExitCode == 0
                ? new EncodeResult(true, request.OutputPath, null)
                : new EncodeResult(false, request.OutputPath, $"HandBrakeCLI code {proc.ExitCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (proc is { HasExited: false })
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Échec de l'encodage de {Input}", request.InputPath);
            return new EncodeResult(false, request.OutputPath, ex.Message);
        }
        finally
        {
            proc?.Dispose();
        }
    }

    private void HandleLine(string? line, IProgress<EncodeProgress>? progress)
    {
        if (string.IsNullOrEmpty(line)) return;
        var m = ProgressRegex().Match(line);
        if (!m.Success || !TryParseNumber(m.Groups["percentage"].Value, out var percentage)) return;

        double? framesPerSecond = null;
        var speed = m.Groups["average"].Success ? m.Groups["average"].Value : m.Groups["current"].Value;
        if (TryParseNumber(speed, out var parsedSpeed)) framesPerSecond = parsedSpeed;
        progress?.Report(new EncodeProgress(percentage, framesPerSecond));
    }

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value.Replace(',', '.'), CultureInfo.InvariantCulture, out number);
}
