using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Prefetch;

namespace NzbWebDAV.Api.Controllers.Prefetch;

public sealed record PrefetchRangeCoverage(long Updated, long RangeBytes, long RangeCachedBytes);
public sealed record PrefetchCoverageResponse(IReadOnlyDictionary<string, PrefetchRangeCoverage> Coverage);

/// <summary>Current revision coverage for the visible jobs; queue status does not wait for this.</summary>
[ApiController]
[Route("api/prefetch/coverage")]
[ProducesResponseType(typeof(PrefetchCoverageResponse), StatusCodes.Status200OK)]
public sealed class PrefetchCoverageController(PrefetchRuntime runtime, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ids = HttpContext.Request.Query["jobIds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length is 0 or > 25 || ids.Any(id => !Guid.TryParse(id, out _)))
            return BadRequest(new { error = "Select between 1 and 25 warming jobs." });
        var ct = HttpContext.RequestAborted;
        await runtime.WaitForInitializationAsync(ct).ConfigureAwait(false);
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        var jobs = runtime.Jobs?.List().Where(job => requested.Contains(job.Id)).ToArray() ?? [];
        var items = (await database.GetItemsByIdsBatchedAsync(jobs.Select(job => job.ItemId).Distinct().ToArray(), ct: ct)
            .ConfigureAwait(false)).ToDictionary(item => item.Id);
        var coverage = await runtime.GetRangeCoverageAsync(jobs, items, ct).ConfigureAwait(false);
        return Ok(new PrefetchCoverageResponse(jobs.Where(job => coverage.ContainsKey(job.Id)).ToDictionary(job => job.Id,
            job => new PrefetchRangeCoverage(job.Updated, coverage[job.Id].Bytes, coverage[job.Id].Cached))));
    }
}
