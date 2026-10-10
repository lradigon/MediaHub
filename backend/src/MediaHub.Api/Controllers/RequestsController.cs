using MediaHub.Api.Contracts;
using MediaHub.Core;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using MediaHub.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RequestsController(AppDbContext db, ITmdbClient tmdb, LibraryService library) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<MediaRequest>> List([FromQuery] RequestStatus? status, CancellationToken ct)
    {
        var q = db.MediaRequests.AsQueryable();
        if (status is not null) q = q.Where(r => r.Status == status);
        return await q.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
    }

    /// <summary>Un ami formule une requête d'ajout (statut Pending).</summary>
    [HttpPost]
    public async Task<ActionResult<MediaRequest>> Create([FromBody] CreateRequestBody body, CancellationToken ct)
    {
        var info = body.Type == MediaType.Movie
            ? await tmdb.GetMovieAsync(body.TmdbId, ct)
            : await tmdb.GetTvAsync(body.TmdbId, ct);
        if (info is null) return NotFound($"TMDB {body.TmdbId} introuvable.");

        var request = new MediaRequest
        {
            TmdbId = body.TmdbId,
            Type = body.Type,
            Title = info.Title,
            Year = info.Year,
            PosterPath = info.PosterPath,
            RequestedByUserId = body.RequestedByUserId ?? Guid.Empty
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { status = RequestStatus.Pending }, request);
    }

    /// <summary>L'admin approuve : crée le média et lance la pipeline.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var request = await db.MediaRequests.FindAsync([id], ct);
        if (request is null) return NotFound();

        var media = await library.AddFromTmdbAsync(request.TmdbId, request.Type, ct);
        request.Status = RequestStatus.Approved;
        request.DecidedAt = DateTimeOffset.UtcNow;
        request.MediaItemId = media.Id;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var request = await db.MediaRequests.FindAsync([id], ct);
        if (request is null) return NotFound();
        request.Status = RequestStatus.Rejected;
        request.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
