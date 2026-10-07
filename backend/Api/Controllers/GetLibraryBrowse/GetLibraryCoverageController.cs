using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Api.Controllers.GetLibraryBrowse;

public sealed record LibraryCoverageResponse(IReadOnlyDictionary<Guid, int?> Coverage);

/// <summary>Optional coverage for at most one page, independent of catalog browsing.</summary>
[ApiController]
[Route("api/get-library-coverage")]
[ProducesResponseType(typeof(LibraryCoverageResponse), StatusCodes.Status200OK)]
public sealed class GetLibraryCoverageController(DavDatabaseClient database, NativeCacheService native,
    ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        using var timing = new NzbWebDAV.Services.Observability.PageLoadTiming("library.coverage");
        if (!config.IsMediaLibraryEnabled()) return NotFound();
        var requested = HttpContext.Request.Query["itemIds"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (requested.Length is 0 or > 50 || requested.Any(id => !Guid.TryParse(id, out _)))
            return BadRequest(new { error = "Select between 1 and 50 imported files." });
        var ct = HttpContext.RequestAborted;
        var items = await database.GetItemsByIdsBatchedAsync(requested.Select(Guid.Parse).Distinct().ToArray(), ct: ct)
            .ConfigureAwait(false);
        var result = new Dictionary<Guid, int?>();
        foreach (var item in items)
        {
            int? value = null;
            try { value = await native.GetCurrentCoverageAsync(item, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
                or NzbWebDAV.Exceptions.CorruptedBlobPayloadException or ObjectDisposedException)
            { /* Optional enrichment must not hide the catalog. */ }
            result[item.Id] = value;
        }
        return Ok(new LibraryCoverageResponse(result));
    }
}
