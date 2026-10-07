using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.NativeCache;

[ApiController]
[Route("api/native-cache")]
public sealed class NativeCacheController(NativeCacheService native, NativeCacheOperations operations, ConfigManager config) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (string.Equals(HttpContext.Request.Query["modeOnly"], "true", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ActiveMode = native.ActiveMode.ToString().ToLowerInvariant() });
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new
        {
            ActiveMode = native.ActiveMode.ToString().ToLowerInvariant(),
            ConfiguredMode = config.GetCacheMode().ToString().ToLowerInvariant(),
            RestartRequired = native.RequiresRestart(config),
            native.InitializationError,
            native.InitializationPending,
            native.ReservedBufferBytes,
            Counters = native.Statistics.Snapshot(),
            Folders = native.Store is null ? [] : await native.Store.GetStatusAsync(HttpContext.RequestAborted).ConfigureAwait(false),
            Jobs = operations.GetJobs()
        });
    }
}

[ApiController]
[Route("api/native-cache/operations")]
public sealed class NativeCacheOperationController(NativeCacheOperations operations, NativeCacheService native) : PostOnlyApiController
{
    public sealed record OperationRequest(string? FolderId, string Operation, string? ConfirmFolderId, string? JobId,
        string? CacheKey, bool? Pinned, string? ConfirmCacheKey);
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var request = await HttpContext.Request.ReadFromJsonAsync<OperationRequest>(HttpContext.RequestAborted).ConfigureAwait(false)
            ?? throw new ArgumentException("An operation is required.");
        if (request.Operation == "pin")
        {
            if (native.Store is null || request.CacheKey is null || request.Pinned is null) throw new ArgumentException("Select an active cache entry and retention state.");
            await native.Store.SetPinnedKeyAsync(request.CacheKey, request.Pinned.Value, HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new { status = true });
        }
        return request.Operation == "cancel" ? Ok(new { Cancelled = operations.Cancel(request.JobId ?? "") })
            : Accepted(value: operations.Enqueue(request.FolderId ?? "", request.Operation, request.ConfirmFolderId,
                request.CacheKey, request.ConfirmCacheKey));
    }
}

[ApiController]
[Route("api/native-cache/entries")]
public sealed class NativeCacheEntriesController(NativeCacheService native, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (native.Store is null) throw new ArgumentException("Native cache must be active to browse its catalogue.");
        var query = HttpContext.Request.Query;
        var limit = int.TryParse(query["limit"], out var requested) ? requested : 50;
        var page = await native.Store.ListEntriesAsync(query["folderId"].ToString(), query["after"].ToString(), limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var ids = page.Select(entry => Guid.TryParse(entry.ItemId, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToArray();
        var items = await database.GetItemsByIdsBatchedAsync(ids, ct: HttpContext.RequestAborted).ConfigureAwait(false);
        var names = items.ToDictionary(item => item.Id.ToString("N"), item => item.Name);
        return Ok(new
        {
            entries = page.Select(entry => new
            {
                entry.Key,
                entry.FolderId,
                entry.ItemId,
                name = names.GetValueOrDefault(entry.ItemId) ?? entry.ItemId,
                entry.Length,
                entry.AllocatedBytes,
                entry.VerifiedBytes,
                entry.Pinned,
                entry.Generation
            }),
            nextAfter = page.Count == limit ? page[^1].Key : null
        });
    }
}

[ApiController]
[Route("api/native-cache/ranges")]
public sealed class NativeCacheRangesController(NativeCacheService native) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        await native.WaitForInitializationAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        if (native.Store is null) throw new ArgumentException("Native cache must be active to browse its catalogue.");
        var query = HttpContext.Request.Query;
        var limit = query.ContainsKey("limit")
            ? int.TryParse(query["limit"], out var requested) ? requested : throw new ArgumentException("Invalid range page limit.") : 50;
        var after = query.ContainsKey("afterOffset")
            ? long.TryParse(query["afterOffset"], out var offset) ? offset : throw new ArgumentException("Invalid range cursor.") : -1;
        var ranges = await native.Store.ListVerifiedRangesAsync(query["key"].ToString(), after, limit, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { ranges, nextAfter = ranges.Count == limit ? (long?)ranges[^1].Offset : null });
    }
}

[ApiController]
[Route("api/native-cache/file-ranges")]
public sealed class NativeCacheFileRangesController(NativeCacheService native, DavDatabaseClient database) : GetOnlyApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        if (!Guid.TryParse(HttpContext.Request.Query["itemId"], out var itemId))
            throw new ArgumentException("Select an imported file to view cache ranges.");
        await native.WaitForInitializationAsync(ct).ConfigureAwait(false);
        var item = await database.GetFileById(itemId.ToString()).ConfigureAwait(false);
        if (item is null) return NotFound(new { error = "The selected file is no longer available." });
        var identity = await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false);
        if (identity is null || native.Store is not { } store)
            return Ok(new { available = false });
        var map = await store.GetRangeMapAsync(identity, ct).ConfigureAwait(false);
        // A repair/source change during the read invalidates the entire display snapshot.
        if (await native.GetCurrentCacheIdentityAsync(item, ct).ConfigureAwait(false) != identity)
            return Ok(new { available = false });
        return Ok(new { available = true, map.Length, map.CachedBytes, map.Ranges, map.Complete });
    }
}
