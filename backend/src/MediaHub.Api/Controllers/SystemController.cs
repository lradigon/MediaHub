using System.Diagnostics;
using MediaHub.Core.Abstractions;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Options;
using MediaHub.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MediaHub.Api.Controllers;

public record ServiceStatusDto(string Name, string State, string Detail, long LatencyMilliseconds);

[ApiController]
[Route("api/system")]
public class SystemController(
    AppDbContext db,
    ITmdbClient tmdb,
    ITorznabClient torznab,
    IDownloadClient download,
    IOptions<TmdbOptions> tmdbOptions,
    IOptions<TorznabOptions> torznabOptions,
    IOptions<LibraryOptions> libraryOptions,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IReadOnlyList<ServiceStatusDto>> Status(CancellationToken ct)
    {
        return await Task.WhenAll(
            ProbeAsync("PostgreSQL", async token =>
            {
                if (!await db.Database.CanConnectAsync(token))
                    throw new InvalidOperationException("Base inaccessible.");
            }, true, ct),
            ProbeAsync("TMDB", tmdb.CheckConnectionAsync, !string.IsNullOrWhiteSpace(tmdbOptions.Value.ApiKey), ct),
            ProbeAsync("C411", torznab.CheckConnectionAsync, !string.IsNullOrWhiteSpace(torznabOptions.Value.ApiKey), ct),
            ProbeAsync("qBittorrent", download.CheckConnectionAsync, true, ct),
            ProbeAsync("Stockage", token =>
            {
                token.ThrowIfCancellationRequested();
                StorageAvailability.EnsureAvailable(libraryOptions.Value);
                return Task.CompletedTask;
            }, true, ct),
            ProbeAsync("Worker", async token =>
            {
                var path = configuration["Pipeline:HeartbeatPath"]!;
                var heartbeat = await System.IO.File.ReadAllTextAsync(path, token);
                if (!DateTimeOffset.TryParse(heartbeat, out var lastSeen) ||
                    DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(60))
                    throw new InvalidOperationException("Heartbeat périmé.");
            }, !string.IsNullOrWhiteSpace(configuration["Pipeline:HeartbeatPath"]), ct));
    }

    private static async Task<ServiceStatusDto> ProbeAsync(
        string name, Func<CancellationToken, Task> probe, bool configured, CancellationToken ct)
    {
        if (!configured) return new(name, "unconfigured", "Non configuré", 0);
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await probe(timeout.Token);
            return new(name, "up", "Disponible", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(name, "down", "Délai de réponse dépassé", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var detail = ex switch
            {
                UnauthorizedAccessException => "Authentification refusée",
                HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                FileNotFoundException => "Heartbeat absent",
                _ => "Service indisponible ou réponse invalide"
            };
            return new(name, "down", detail, stopwatch.ElapsedMilliseconds);
        }
    }
}