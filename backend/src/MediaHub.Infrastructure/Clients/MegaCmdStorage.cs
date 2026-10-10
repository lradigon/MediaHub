using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace MediaHub.Infrastructure.Clients;

/// <summary>
/// Stockage MEGA via MEGAcmd (mega-put / mega-ls). MEGAcmd doit être installé
/// et authentifié sur l'hôte (mega-login) au préalable.
/// </summary>
public class MegaCmdStorage(IOptions<MegaOptions> options) : ICloudStorage
{
    private readonly MegaOptions _opt = options.Value;

    public async Task<string> UploadAsync(
        string localPath,
        string remoteFolder,
        string? remoteFileName = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var remoteFolderPath = BuildRemotePath(remoteFolder);
        await EnsureFolderAsync(remoteFolderPath, ct);
        var remote = string.IsNullOrWhiteSpace(remoteFileName)
            ? remoteFolderPath
            : $"{remoteFolderPath}/{remoteFileName}";
        var exe = CommandPath("mega-put");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(localPath);
        psi.ArgumentList.Add(remote);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Impossible de démarrer {exe}");
        using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var monitoring = MonitorUploadProgressAsync(localPath, progress, monitorCts.Token);
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await WaitForExitAsync(proc, ct);
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

        var code = proc.ExitCode;
        var err = await errTask;
        if (code != 0)
            throw new InvalidOperationException($"mega-put a échoué ({code}): {err}");
        await outTask;
        progress?.Report(100);
        return string.IsNullOrWhiteSpace(remoteFileName)
            ? $"{remoteFolderPath}/{Path.GetFileName(localPath)}"
            : remote;
    }

    public async Task<IReadOnlyList<CloudEntry>> ListAsync(string remoteFolder, CancellationToken ct = default)
    {
        var remote = BuildRemotePath(remoteFolder);
        var exe = CommandPath("mega-find");
        var (code, output, err) = await RunAsync(exe, [remote], ct);
        if (code != 0 && string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"mega-find a échoué ({code}): {err}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => new CloudEntry(path, 0))
            .ToList();
    }

    private async Task EnsureFolderAsync(string remoteFolder, CancellationToken ct)
    {
        var exe = CommandPath("mega-mkdir");
        var (code, _, err) = await RunAsync(exe, ["-p", remoteFolder], ct);
        if (code == 54 && err.Contains("Folder already exists", StringComparison.OrdinalIgnoreCase))
            return;
        if (code != 0)
            throw new InvalidOperationException($"mega-mkdir a échoué ({code}): {err}");
    }

    private string BuildRemotePath(string remoteFolder) =>
        $"/{_opt.RemoteRoot.Trim('/')}/{remoteFolder.Trim('/')}";

    private string CommandPath(string command)
    {
        var path = Path.Combine(_opt.MegaCmdDir, command);
        var batchPath = $"{path}.bat";
        return OperatingSystem.IsWindows() && File.Exists(batchPath) ? batchPath : path;
    }

    private async Task MonitorUploadProgressAsync(string localPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (progress is null) return;

        var localFileName = Path.GetFileName(localPath);
        var lastProgress = -1d;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var (code, output, _) = await RunAsync(
                CommandPath("mega-transfers"),
                ["--only-uploads", "--path-display-size=200"],
                ct);
            if (code != 0) continue;

            var transferLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line.Contains(localFileName, StringComparison.OrdinalIgnoreCase));
            var match = transferLine is null
                ? null
                : Regex.Match(transferLine, @"(?<progress>\d+(?:[.,]\d+)?)%\s+of\b");
            if (match is null ||
                !double.TryParse(match.Groups["progress"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                continue;

            value = Math.Clamp(value, 0, 100);
            if (value == lastProgress) continue;
            progress.Report(value);
            lastProgress = value;
        }
    }

    private static async Task<(int Code, string Out, string Err)> RunAsync(
        string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Impossible de démarrer {exe}");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await WaitForExitAsync(proc, ct);
        return (proc.ExitCode, await outTask, await errTask);
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
