using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Api.Controllers;

/// <summary>Parameters for a bounded or incremental stock inspection.</summary>
public sealed record InspectStockRequest([Range(1, 10000)] int? MaxFiles = null, bool Force = false, Guid? FileId = null);

/// <summary>Progress of the persistent stock inspection.</summary>
public sealed record StockInspectionResponse(string Status, DateTimeOffset RequestedAt, DateTimeOffset? FinishedAt,
    int TotalFiles, int ProcessedFiles, int InspectedFiles, int FailedFiles, string? CurrentFile, string? Error);

/// <summary>Cached technical metadata for a stock file.</summary>
public sealed record StockFileResponse(Guid Id, string Name, long SizeBytes, DateTimeOffset ModifiedAt,
    DateTimeOffset? InspectedAt, string Status, bool IsPresent, string? Error, StockMediaFile? Details);

/// <summary>Stock inventory and its last inspection without filesystem access.</summary>
public sealed record StockDetailsResponse(Guid MediaId, string Title, string? PosterPath, bool HasStorage,
    StockInspectionResponse? Inspection, IReadOnlyList<StockFileResponse> Files);

[ApiController]
[Route("api/media/{id:guid}/stock-details")]
public class StockController(AppDbContext db, StockInspectionService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<StockDetailsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockDetailsResponse>> Get(Guid id, CancellationToken ct)
    {
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id, ct);
        if (media is null) return NotFound();
        var scan = await db.StockInspections.AsNoTracking().FirstOrDefaultAsync(value => value.MediaItemId == id, ct);
        var files = await db.StockFileInspections.AsNoTracking().Where(value => value.MediaItemId == id).OrderBy(value => value.Path).ToListAsync(ct);
        return new StockDetailsResponse(id, media.Title, media.PosterPath, !string.IsNullOrWhiteSpace(media.StoragePath),
            scan is null ? null : ToResponse(scan), files.Select(value => new StockFileResponse(value.Id, Path.GetFileName(value.Path),
                value.SizeBytes, value.ModifiedAt, value.InspectedAt, value.Status, value.IsPresent, value.Error,
                value.DetailsJson is null ? null : JsonSerializer.Deserialize<StockMediaFile>(value.DetailsJson))).ToList());
    }

    [HttpPost]
    [ProducesResponseType<StockInspectionResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StockInspectionResponse>> Inspect(Guid id, InspectStockRequest request, CancellationToken ct)
    {
        var media = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(value => value.Id == id, ct);
        if (media is null) return NotFound();
        if (string.IsNullOrWhiteSpace(media.StoragePath)) return Conflict("Aucun dossier monte n'est associe a ce media.");
        var scan = await service.RequestAsync(id, request.MaxFiles, request.Force, request.FileId, ct);
        if (scan is null) return BadRequest("Le fichier choisi n'appartient pas au stock present de ce media.");
        return AcceptedAtAction(nameof(Get), new { id }, ToResponse(scan));
    }

    [HttpPost("cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        if (!await db.MediaItems.AnyAsync(value => value.Id == id, ct)) return NotFound();
        await service.CancelAsync(id, ct);
        return NoContent();
    }

    private static StockInspectionResponse ToResponse(StockInspection scan) => new(scan.Status, scan.RequestedAt, scan.FinishedAt,
        scan.TotalFiles, scan.ProcessedFiles, scan.InspectedFiles, scan.FailedFiles, scan.CurrentFile, scan.Error);
}