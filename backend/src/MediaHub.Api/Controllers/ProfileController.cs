using MediaHub.Api.Contracts;
using MediaHub.Core.Entities;
using MediaHub.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProfileController(AppDbContext db) : ControllerBase
{
    /// <summary>Récupère le profil de qualité par défaut.</summary>
    [HttpGet]
    public async Task<ActionResult<ReleaseProfileDto>> Get(CancellationToken ct)
    {
        var p = await db.ReleaseProfiles.FirstOrDefaultAsync(x => x.IsDefault, ct)
                ?? await db.ReleaseProfiles.FirstOrDefaultAsync(ct);
        if (p is null) return NotFound();
        return Map(p);
    }

    /// <summary>Met à jour le profil de qualité par défaut.</summary>
    [HttpPut]
    public async Task<ActionResult<ReleaseProfileDto>> Update([FromBody] ReleaseProfileDto body, CancellationToken ct)
    {
        var p = await db.ReleaseProfiles.FirstOrDefaultAsync(x => x.IsDefault, ct);
        if (p is null) return NotFound();

        p.Name = string.IsNullOrWhiteSpace(body.Name) ? p.Name : body.Name.Trim();
        p.RequireMulti = body.RequireMulti;
        p.MinSeeders = Math.Max(0, body.MinSeeders);
        p.AllowedResolutions = Clean(body.AllowedResolutions);
        p.AllowedVideoCodecs = Clean(body.AllowedVideoCodecs);
        p.RejectedVideoCodecs = Clean(body.RejectedVideoCodecs);
        p.AllowedSources = Clean(body.AllowedSources);
        p.RejectedTerms = Clean(body.RejectedTerms);

        await db.SaveChangesAsync(ct);
        return Map(p);
    }

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(v => v.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static ReleaseProfileDto Map(ReleaseProfile p) => new(
        p.Id, p.Name, p.RequireMulti, p.MinSeeders,
        p.AllowedResolutions, p.AllowedVideoCodecs, p.RejectedVideoCodecs,
        p.AllowedSources, p.RejectedTerms);
}
