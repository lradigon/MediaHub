using MediaHub.Api.Contracts;
using MediaHub.Core.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace MediaHub.Api.Controllers;

[ApiController]
[Route("api/c411")]
public class C411Controller(ITorznabClient torznab) : ControllerBase
{
    [HttpGet("account")]
    public async Task<ActionResult<C411AccountStatsDto>> Account(CancellationToken ct)
    {
        try
        {
            var stats = await torznab.GetAccountStatsAsync(ct);
            return new C411AccountStatsDto(stats.Username, stats.UploadedBytes, stats.DownloadedBytes, stats.Ratio);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }
}