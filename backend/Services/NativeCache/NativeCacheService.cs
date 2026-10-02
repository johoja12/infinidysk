using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Serilog;
using System.Diagnostics;

namespace NzbWebDAV.Services.NativeCache;

public sealed class NativeCacheService : IAsyncDisposable
{
    private readonly IBlobStore _blobs;
    private readonly RepairPatchStore _repairs;
    private readonly NativeBufferSlots? _bufferSlots;
    private readonly object _lifetimeGate = new();
    private Task _initialization = Task.CompletedTask;
    private Task _cleanup = Task.CompletedTask;
    private NativeCacheStore? _store;
    private NativeCacheCommitQueue? _commitQueue;
    private string? _initializationError;
    private bool _disposed;
    private int _revisionMetadataWarning;
    internal TimeSpan InitializationWait { get; set; } = TimeSpan.FromSeconds(1);
    internal Task InitializationCompletion => Task.WhenAll(_initialization, _cleanup);

    public NativeCacheService(ConfigManager config, IBlobStore blobs, RepairPatchStore repairs)
        : this(config, blobs, repairs, settings => new NativeCacheStore(Path.Combine(settings.MetadataPath, "catalogue.db"), settings.Folders, settings.ChunkMb)) { }

    internal NativeCacheService(ConfigManager config, IBlobStore blobs, RepairPatchStore repairs,
        Func<NativeCacheSettings, NativeCacheStore> storeFactory)
    {
        _blobs = blobs;
        _repairs = repairs;
        ActiveMode = config.GetActiveCacheMode();
        if (ActiveMode != CacheMode.Native) return;
        try
        {
            ActiveSettings = NativeCacheSettings.FromConfig(config);
            if (!ActiveSettings.Folders.Any(folder => folder.Enabled))
                throw new ArgumentException("Configure at least one enabled native cache folder.");
            _bufferSlots = new NativeBufferSlots(NativeBufferSlots.CapacityFor(ActiveSettings.BufferMb));
            _initialization = Task.Run(() => InitializeStoreAsync(ActiveSettings, storeFactory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or ArgumentException)
        {
            _initializationError = "Native cache could not initialize. Check folder permissions and the local metadata path.";
            Log.Warning("Native cache initialization failed ({ErrorType}); source streaming remains available", exception.GetType().Name);
        }
    }

    public CacheMode ActiveMode { get; }
    public NativeCacheStatistics Statistics { get; } = new();
    public NativeCacheSettings? ActiveSettings { get; }
    public string? InitializationError => Volatile.Read(ref _initializationError);
    public bool InitializationPending => !_initialization.IsCompleted;
    public NativeCacheStore? Store => Volatile.Read(ref _store);
    public long QueuedCommitBytes => Volatile.Read(ref _commitQueue)?.QueuedBytes ?? 0;

    /// <summary>
    /// Receives (item, offset, length) ranges a foreground stream served without caching. The
    /// prefetch runtime registers itself here and fills them in later as low-cost warming jobs.
    /// </summary>
    public Action<Guid, long, long>? BackfillSink { get; set; }

    private Action<long, long>? BackfillFor(DavItem item)
    {
        if (BackfillSink is null) return null;
        var id = item.Id;
        return (offset, length) => BackfillSink?.Invoke(id, offset, length);
    }
    public long ReservedBufferBytes => _bufferSlots?.HeldBytes ?? 0;

    /// <summary>Block buffer slots shared by every Native Cache stream; null when the cache is inactive.</summary>
    public NativeBufferSlots? BufferSlots => _bufferSlots;

    private async Task InitializeStoreAsync(NativeCacheSettings settings, Func<NativeCacheSettings, NativeCacheStore> factory)
    {
        try
        {
            var store = factory(settings);
            lock (_lifetimeGate)
            {
                if (!_disposed)
                {
#pragma warning disable CA2000 // Owned by the service; DisposeAsync drains it before the store closes.
                    Volatile.Write(ref _commitQueue, new NativeCacheCommitQueue());
#pragma warning restore CA2000
                    Volatile.Write(ref _store, store);
                    return;
                }
            }
            await store.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Volatile.Write(ref _initializationError, "Native cache could not initialize. Check folder permissions and the local metadata path.");
            Log.Warning("Native cache initialization failed ({ErrorType}); source streaming remains available", exception.GetType().Name);
        }
    }

    public async Task<bool> WaitForInitializationAsync(CancellationToken cancellationToken = default)
    {
        try { await _initialization.WaitAsync(InitializationWait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return Store is not null;
    }

    public bool RequiresRestart(ConfigManager config)
    {
        try
        {
            return ActiveMode != config.GetCacheMode()
                || (ActiveMode == CacheMode.Native && System.Text.Json.JsonSerializer.Serialize(ActiveSettings)
                    != System.Text.Json.JsonSerializer.Serialize(NativeCacheSettings.FromConfig(config)));
        }
        catch (ArgumentException) { return true; } // Keep initialization diagnostics available for malformed optional settings.
    }

    public async Task<Stream> WrapAsync(DavItem item, Func<CancellationToken, Task<Stream>> open, CancellationToken cancellationToken, bool requireNative = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (InitializationPending) await WaitForInitializationAsync(cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var store = Store;
        if (ActiveSettings is { } settings && item.FileSize is { } fileSize &&
            fileSize < settings.MinFileMb * 1024L * 1024L)
        {
            if (requireNative) throw new InvalidOperationException("File is below the configured Native Cache minimum size.");
            return await open(cancellationToken).ConfigureAwait(false);
        }
        if (store is null || _bufferSlots is null || item.FileBlobId is not { } blobId || item.FileSize is not > 0)
        {
            if (requireNative) throw new InvalidOperationException("Native cache admission is unavailable; no source bytes were requested.");
            return await open(cancellationToken).ConfigureAwait(false);
        }

        // Streams take a buffer slot per block, not per response, so playback is always admitted.
        // Warming is deferred up front when it could not take one now.
        if (requireNative && !_bufferSlots.CanAdmitBackground())
            throw new InvalidOperationException("Native cache admission is unavailable; no source bytes were requested.");
        var watch = ContentRevisionTracker.Watch(blobId);
        ContentRevisionTracker.RevisionWatch? owned = watch;
        try
        {
            var identityStarted = Stopwatch.GetTimestamp();
            var current = await GetCurrentIdentityAsync(item, blobId, watch, cancellationToken).ConfigureAwait(false);
            StreamStartupTrace.TryRecord(StreamStartupPhase.NativeIdentity,
                elapsed: Stopwatch.GetElapsedTime(identityStarted));
            if (current is { } cached)
            {
                var stream = new NativeCachedStream(store, cached.Identity, open,
                    () => watch.IsCurrent && cached.Revision.IsCurrent, _bufferSlots, background: requireNative, statistics: Statistics, writeBehind: true,
                    commitQueue: Volatile.Read(ref _commitQueue), backfill: requireNative ? null : BackfillFor(item), ownedResource: watch);
                owned = null; // The returned stream owns the watch.
                return stream;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException
            or NzbWebDAV.Exceptions.CorruptedBlobPayloadException)
        {
            // Only cache metadata failures fall back. A failure opening the actual
            // source below must not be mistaken for a cache failure and retried.
        }
        finally { owned?.Dispose(); }
        if (requireNative) throw new InvalidOperationException("Native cache metadata is unavailable; no source bytes were requested.");
        return await open(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DateTime> GetLastModifiedAsync(DavItem item, CancellationToken ct)
    {
        try { return await GetRevisionModifiedAsync(item, ct).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException
            or NzbWebDAV.Exceptions.CorruptedBlobPayloadException)
        {
            if (Interlocked.Exchange(ref _revisionMetadataWarning, 1) == 0)
                Log.Warning("Native revision metadata is unavailable; clients will revalidate source content. Reason: {Reason}", exception.GetType().Name);
            return DateTime.UtcNow;
        }
    }

    private async Task<DateTime> GetRevisionModifiedAsync(DavItem item, CancellationToken ct)
    {
        if (InitializationPending) await WaitForInitializationAsync(ct).ConfigureAwait(false);
        if (Store is not { } store || item.FileBlobId is not { } blobId) return item.CreatedAt;
        using var watch = ContentRevisionTracker.Watch(blobId);
        var current = await GetCurrentIdentityAsync(item, blobId, watch, ct).ConfigureAwait(false);
        if (current is not { } cached) return item.CreatedAt;
        if (!watch.IsCurrent || !cached.Revision.IsCurrent) throw new IOException("Media revision is changing; retry metadata refresh.");
        return await store.ModificationTimeAsync(item.Id.ToString("N"),
            cached.Identity.Generation, item.CreatedAt, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The cache identity of the item's current source revision, without admitting a buffer or
    /// opening the source. Null when the cache is inactive or the revision is unknown or changing.
    /// </summary>
    public async Task<NativeCacheIdentity?> GetCurrentCacheIdentityAsync(DavItem item, CancellationToken cancellationToken = default)
    {
        if (Store is null || item.FileBlobId is not { } blobId || item.FileSize is not > 0) return null;
        using var watch = ContentRevisionTracker.Watch(blobId);
        var current = await GetCurrentIdentityAsync(item, blobId, watch, cancellationToken).ConfigureAwait(false);
        return current is { } cached && watch.IsCurrent && cached.Revision.IsCurrent ? cached.Identity : null;
    }

    public async Task<int?> GetCurrentCoverageAsync(DavItem item, CancellationToken cancellationToken = default)
    {
        var store = Store;
        if (store is null || item.FileBlobId is not { } blobId || item.FileSize is not > 0) return null;
        using var watch = ContentRevisionTracker.Watch(blobId);
        var current = await GetCurrentIdentityAsync(item, blobId, watch, cancellationToken).ConfigureAwait(false);
        if (current is null || !watch.IsCurrent || !current.Value.Revision.IsCurrent) return null;
        var bytes = await store.GetCoverageAsync(current.Value.Identity, cancellationToken).ConfigureAwait(false);
        return watch.IsCurrent && current.Value.Revision.IsCurrent
            ? (int)Math.Floor(Math.Min(100.0, bytes * 100.0 / item.FileSize.Value)) : null;
    }

    private async Task<(NativeCacheIdentity Identity, RepairRevisionStore.Snapshot Revision)?> GetCurrentIdentityAsync(
        DavItem item, Guid blobId, ContentRevisionTracker.RevisionWatch watch, CancellationToken cancellationToken)
    {
        if (item.SubType == DavItem.ItemSubType.MultipartFile)
            return await GetMultipartIdentityAsync(item, blobId, watch).ConfigureAwait(false);

        await using var blob = _blobs.ReadBlob(blobId);
        if (blob is null || !watch.IsCurrent) return null;
        var hash = await SHA256.HashDataAsync(blob, cancellationToken).ConfigureAwait(false);
        var dependencies = await GetSourceSegmentsAsync(item, blobId).ConfigureAwait(false);
        if (dependencies is null || !watch.IsCurrent || item.FileSize is not > 0) return null;
        var revision = _repairs.CaptureNativeRevisions(dependencies);
        return (new NativeCacheIdentity(item.Id.ToString("N"),
            $"v2:{blobId:N}:{Convert.ToHexString(hash)}:{revision.Fingerprint}", item.FileSize.Value)
            { DisplayName = item.Name }, revision);
    }

    // Multipart blobs are rewritten in place by lazy RAR resolution, which only fills in byte
    // ranges for volumes the blob already references. A raw blob hash would start a new cache
    // generation on every resolved volume, so derive the generation from content-defining
    // fields instead (v3). Single-blob kinds keep their v2 raw-hash identity.
    private async Task<(NativeCacheIdentity Identity, RepairRevisionStore.Snapshot Revision)?> GetMultipartIdentityAsync(
        DavItem item, Guid blobId, ContentRevisionTracker.RevisionWatch watch)
    {
        var multipart = await _blobs.ReadBlob<DavMultipartFile>(blobId).ConfigureAwait(false);
        if (multipart?.Metadata is not { } metadata || !watch.IsCurrent || item.FileSize is not > 0) return null;
        var content = MultipartContentIdentity.Get(metadata);
        var revision = _repairs.CaptureNativeRevisions(MultipartSegments(metadata));
        return (new NativeCacheIdentity(item.Id.ToString("N"),
            $"v3:{blobId:N}:{content}:{revision.Fingerprint}", item.FileSize.Value)
            { DisplayName = item.Name }, revision);
    }

    private static IEnumerable<string> Segments(string[] ids, string[][]? fallbacks) => ids.Concat(
        fallbacks?.Where(row => row is not null).SelectMany(row => row) ?? []);

    private static IEnumerable<string> MultipartSegments(DavMultipartFile.Meta metadata) =>
        (metadata.FileParts ?? []).SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds))
            .Concat((metadata.PendingParts ?? []).SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds)));

    private async Task<IEnumerable<string>?> GetSourceSegmentsAsync(DavItem item, Guid blobId)
    {
        switch (item.SubType)
        {
            case DavItem.ItemSubType.NzbFile:
                var nzb = await _blobs.ReadBlob<DavNzbFile>(blobId).ConfigureAwait(false);
                return nzb?.SegmentIds is { } ids ? Segments(ids, nzb.SegmentFallbackIds) : null;
            case DavItem.ItemSubType.RarFile:
                var rar = await _blobs.ReadBlob<DavRarFile>(blobId).ConfigureAwait(false);
                return rar?.RarParts?.SelectMany(part => Segments(part.SegmentIds, part.SegmentFallbackIds));
            default: return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            var store = _store;
            var commitQueue = _commitQueue;
            Volatile.Write(ref _store, null);
            Volatile.Write(ref _commitQueue, null);
            if (store is not null) _cleanup = Task.Run(async () =>
            {
                // Drain queued commits before the catalogue closes; unpublished blocks are reported
                // to their streams' backfill requests.
                try { if (commitQueue is not null) await commitQueue.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Log.Warning("Native cache commit queue shutdown failed ({ErrorType})", exception.GetType().Name); }
                try { await store.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { Log.Warning("Native cache shutdown failed ({ErrorType})", exception.GetType().Name); }
            });
        }
        try { await _cleanup.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { /* A stuck NAS must not hold host shutdown; cleanup remains owned. */ }
        // Active response leases can finish during host shutdown. Buffer slots have
        // no native handle; let remaining leases release them before collection.
    }
}
