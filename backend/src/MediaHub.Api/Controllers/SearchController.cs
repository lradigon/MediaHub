using MediaHub.Api.Contracts;
using MediaHub.Core.Abstractions;
using MediaHub.Core.Entities;
using MediaHub.Core.Releases;
using MediaHub.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController(ITmdbClient tmdb, ITorznabClient torznab, AppDbContext db) : ControllerBase
{
    /// <summary>Recherche de films/séries via TMDB.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<TmdbSearchItem>> Search([FromQuery] string query, CancellationToken ct)
        => await tmdb.SearchAsync(query, ct);

    /// <summary>Recherche de releases via l'indexeur, évaluées contre le profil par défaut.</summary>
    [HttpGet("releases")]
    public async Task<IReadOnlyList<ReleaseDto>> SearchReleases([FromQuery] string query, CancellationToken ct)
    {
        var profile = await db.ReleaseProfiles.FirstOrDefaultAsync(p => p.IsDefault, ct)
            ?? new ReleaseProfile();

        var releases = await torznab.SearchAsync(query, ct: ct);

        return releases
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
    }
}
