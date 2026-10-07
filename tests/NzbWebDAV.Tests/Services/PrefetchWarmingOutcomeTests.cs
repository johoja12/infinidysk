using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Library;
using NzbWebDAV.Services.Plex;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// End-to-end prefetch behaviour over a real Native Cache, job store and policy service:
/// own-range coverage, backfill coalescing, damaged-release hand-off, and finish-watched.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class PrefetchWarmingOutcomeTests
{
    private const long MiB = 1024L * 1024;

    [Fact]
    public async Task BrowseWithoutCoverage_PreservesCountsAndDoesNotReadSourceMetadata()
    {
        var reads = 0;
        await using var harness = await Harness.CreateAsync(fileSize: NativeCacheStore.BlockSize, onBlobRead: () => reads++);
        harness.Item.Path = "/content/movie.mkv";
        await using var context = new DavDatabaseContext(harness.Options);
        context.Items.Update(harness.Item);
        await context.SaveChangesAsync();
        var identity = (await harness.Native.GetCurrentCacheIdentityAsync(harness.Item))!;
        Assert.True(await harness.Native.Store!.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        var service = new LibraryBrowseService(new LibraryCatalogService(context), new EmptyMetadataIndex(), harness.Native);
        reads = 0;
        var fast = await service.QueryAsync(new LibraryBrowseQuery { View = "files", Category = "all", IncludeCoverage = false });
        Assert.Equal(1, fast.TotalItems);
        Assert.Null(Assert.Single(fast.Files!).CachePercentage);
        Assert.Equal(0, reads);
        var enriched = await service.QueryAsync(new LibraryBrowseQuery { View = "files", Category = "all" });
        Assert.Equal(fast.TotalItems, enriched.TotalItems);
        Assert.Equal(100, Assert.Single(enriched.Files!).CachePercentage);
        Assert.True(reads > 0);
    }

    [Fact]
    public async Task RangeCoverage_SourceReplacementImmediatelyDropsOldCoverage()
    {
        await using var harness = await Harness.CreateAsync(fileSize: NativeCacheStore.BlockSize);
        var jobs = harness.Runtime.Jobs!;
        var range = jobs.Enqueue(harness.Item.Id, "manual", 50, 0, NativeCacheStore.BlockSize);
        var items = new Dictionary<Guid, DavItem> { [harness.Item.Id] = harness.Item };
        var identity = (await harness.Native.GetCurrentCacheIdentityAsync(harness.Item))!;
        Assert.True(await harness.Native.Store!.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        var before = await harness.Runtime.GetRangeCoverageAsync(jobs.List(), items, CancellationToken.None);
        Assert.Equal((long)NativeCacheStore.BlockSize, before[range.Id].Cached);
        await harness.ReplaceSourceAsync();
        var after = await harness.Runtime.GetRangeCoverageAsync(jobs.List(), items, CancellationToken.None);
        Assert.Equal(0, after[range.Id].Cached);
    }

    [Fact]
    public async Task RangeCoverage_ReportsTheJobsOwnRangeForTheCurrentRevision()
    {
        await using var harness = await Harness.CreateAsync(fileSize: 2L * NativeCacheStore.BlockSize + 10);
        var jobs = harness.Runtime.Jobs!;
        var range = jobs.Enqueue(harness.Item.Id, PrefetchRuntime.BackfillOwner, 60, NativeCacheStore.BlockSize, NativeCacheStore.BlockSize);
        Assert.Equal(range.Id, jobs.ClaimNext()!.Id); // Running, so the whole-file request below stays separate.
        var whole = jobs.Enqueue(harness.Item.Id, "manual", 50, 0, 0);
        Assert.True(range.IsRangeJob);
        Assert.False(whole.IsRangeJob);
        var items = new Dictionary<Guid, DavItem> { [harness.Item.Id] = harness.Item };

        var before = await harness.Runtime.GetRangeCoverageAsync(jobs.List(), items, CancellationToken.None);
        // Whole-file jobs keep whole-file coverage only, so only the range job is reported.
        Assert.Equal(((long)NativeCacheStore.BlockSize, 0L), Assert.Single(before).Value);

        var identity = (await harness.Native.GetCurrentCacheIdentityAsync(harness.Item))!;
        Assert.True(await harness.Native.Store!.WriteBlockAsync(identity, NativeCacheStore.BlockSize, new byte[NativeCacheStore.BlockSize]));
        // A block outside the job's range does not count toward it.
        Assert.True(await harness.Native.Store.WriteBlockAsync(identity, 0, new byte[NativeCacheStore.BlockSize]));
        var after = await harness.Runtime.GetRangeCoverageAsync(jobs.List(), items, CancellationToken.None);
        Assert.Equal(((long)NativeCacheStore.BlockSize, (long)NativeCacheStore.BlockSize), after[range.Id]);
        Assert.False(after.ContainsKey(whole.Id));
    }

    [Fact]
    public async Task BackfillSink_DebouncesOneSessionIntoASingleJob_AndReopensItAfterCompletion()
    {
        await using var harness = await Harness.CreateAsync(fileSize: 6L * NativeCacheStore.BlockSize);
        var jobs = harness.Runtime.Jobs!;
        // Six per-block skips (commit failures, admission timeouts) from one playback session.
        for (var block = 0; block < 6; block++)
            harness.Native.BackfillSink!(harness.Item.Id, block * NativeCacheStore.BlockSize, NativeCacheStore.BlockSize,
                block == 0 ? BackfillMissReasons.UncachedRead : BackfillMissReasons.BuffersFull);
        Assert.Empty(jobs.List()); // Debounced: nothing is written while the session is active.
        Assert.Equal(1, harness.Runtime.PendingBackfillItems);

        await harness.Runtime.FlushBackfillAsync(all: true, CancellationToken.None);
        var job = Assert.Single(jobs.List());
        Assert.Equal((0L, 6L * NativeCacheStore.BlockSize), (job.Start, job.Length));
        // The specific cause wins over the generic fallback; no Plex session was seen.
        Assert.Equal(BackfillMissReasons.BuffersFull, job.MissReason);
        Assert.Null(job.ViewerPlayer);

        await harness.Runtime.Coordinator!.RunOnceAsync(CancellationToken.None);
        Assert.Equal("completed", Assert.Single(jobs.List()).State);

        // A later miss of the same revision reopens the completed row instead of adding another,
        // labelled with the latest playback.
        harness.Runtime.Viewers.Note(harness.Item.Id, new PlaybackViewer("alex", "Living Room TV", 3_120_000));
        harness.Native.BackfillSink!(harness.Item.Id, 0, NativeCacheStore.BlockSize, BackfillMissReasons.WriteFailed);
        await harness.Runtime.FlushBackfillAsync(all: true, CancellationToken.None);
        var reopened = Assert.Single(jobs.List());
        Assert.Equal(job.Id, reopened.Id);
        Assert.Equal("queued", reopened.State);
        Assert.Equal(BackfillMissReasons.WriteFailed, reopened.MissReason);
        Assert.Equal(("alex", "Living Room TV", 3_120_000L), (reopened.ViewerUser, reopened.ViewerPlayer, reopened.MediaDurationMs));
    }

    [Fact]
    public async Task DamagedRelease_FailsWithReason_QueuesRepair_AndIsNotReEnqueuedByPolicies()
    {
        await using var harness = await Harness.CreateAsync(fileSize: 3, source: () => new MissingArticleSource());
        var repaired = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Repairs.CompletionHook = id => { repaired.TrySetResult(id); return Task.CompletedTask; };
        var jobs = harness.Runtime.Jobs!;
        jobs.Enqueue(harness.Item.Id, "manual", 50);

        await harness.Runtime.Coordinator!.RunOnceAsync(CancellationToken.None);

        var failed = Assert.Single(jobs.List());
        Assert.Equal("failed", failed.State);
        Assert.Equal(PrefetchFailureCodes.SourceDamaged, failed.FailureCode);
        Assert.Equal(PrefetchRemedies.RepairQueued, failed.Remedy);
        Assert.StartsWith("Release damaged on Usenet.", failed.Error);
        Assert.Contains("Queued for repair.", failed.Error);
        Assert.Equal(harness.Item.Id, await repaired.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await using (var context = new DavDatabaseContext(harness.Options))
            Assert.Equal(DateTimeOffset.UnixEpoch, (await context.Items.FindAsync(harness.Item.Id))!.NextHealthCheck);

        // The selected Plex hub still lists the file, but the policy refresh leaves it to repair.
        harness.ConfigurePlexSource();
        using var policies = harness.Policies();
        await policies.SyncAsync(true, CancellationToken.None);
        Assert.Equal(failed.Id, Assert.Single(jobs.List()).Id);
        Assert.True(await harness.Runtime.IsKnownDamagedAsync(harness.Item, CancellationToken.None));

        // Backfill from playback of the damaged file is suppressed as well.
        harness.Native.BackfillSink!(harness.Item.Id, 0, 3, BackfillMissReasons.UncachedRead);
        await harness.Runtime.FlushBackfillAsync(all: true, CancellationToken.None);
        Assert.Single(jobs.List());
    }

    [Theory]
    [InlineData(0, true)] // No retries left: the unverified block is a damaged release.
    [InlineData(3, false)] // Retries remain: deferred with a classified reason.
    public async Task RepeatedlyUnverifiedSource_EscalatesToDamageOnTheLastRetry(int maxRetries, bool escalates)
    {
        await using var harness = await Harness.CreateAsync(fileSize: 3, source: () => new UnverifiedSource(),
            settings: new PrefetchSettings { MaxRetries = maxRetries });
        var jobs = harness.Runtime.Jobs!;
        jobs.Enqueue(harness.Item.Id, "manual", 50);

        await harness.Runtime.Coordinator!.RunOnceAsync(CancellationToken.None);

        var job = Assert.Single(jobs.List());
        Assert.Equal(escalates ? "failed" : "queued", job.State);
        Assert.Equal(escalates ? PrefetchFailureCodes.SourceDamaged : PrefetchFailureCodes.SourceUnverified, job.FailureCode);
        Assert.Contains("verif", job.Error);
        Assert.Equal(escalates, jobs.IsDamaged(harness.Item.Id, null, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinishWatched_IsOptIn(bool enabled)
    {
        await using var harness = await Harness.CreateAsync(fileSize: 100 * MiB,
            settings: new PrefetchSettings { Enabled = true, FinishWatchedEnabled = enabled, FinishWatchedPercent = 10 });
        harness.Watch(servedBytes: 20 * MiB + FinishWatchedMinimumBytes());
        using var policies = harness.Policies();

        await policies.SyncAsync(true, CancellationToken.None);

        if (!enabled)
        {
            Assert.Empty(harness.Runtime.Jobs!.List());
            return;
        }
        var job = Assert.Single(harness.Runtime.Jobs!.List());
        Assert.Equal(PlexPrefetchService.FinishWatchedOwner, job.Trigger);
        Assert.Equal((0L, 0L), (job.Start, job.Length)); // The rest of the file, not just a range.
        Assert.Equal("Finish partially watched", PlexPrefetchService.SourceLabel(job.Trigger));
    }

    [Fact]
    public void FinishWatched_IgnoresShortOrFastSessions()
    {
        var settings = new PrefetchSettings { Enabled = true, FinishWatchedEnabled = true, FinishWatchedPercent = 10 };
        var now = DateTimeOffset.UtcNow;
        ActiveReadRegistry.Entry Session(TimeSpan age, long served) =>
            new() { FileSize = 1000 * MiB, StartedAt = now - age, BytesRead = served };
        Assert.True(PlexPrefetchService.QualifiesAsWatched(Session(TimeSpan.FromMinutes(5), 100 * MiB), settings, now));
        Assert.False(PlexPrefetchService.QualifiesAsWatched(Session(TimeSpan.FromMinutes(5), 99 * MiB), settings, now));
        // A library scan reading the same bytes within seconds is not a viewing session.
        Assert.False(PlexPrefetchService.QualifiesAsWatched(Session(TimeSpan.FromSeconds(20), 500 * MiB), settings, now));
    }

    [Fact]
    public async Task FinishWatchedWork_SurvivesPolicyChangesWhileEnabled_AndIsPrunedWhenDisabled()
    {
        await using var harness = await Harness.CreateAsync(fileSize: 100 * MiB,
            settings: new PrefetchSettings { Enabled = true, FinishWatchedEnabled = true, PauseDuringPlayback = true });
        harness.Watch(servedBytes: 20 * MiB + FinishWatchedMinimumBytes());
        using var policies = harness.Policies();
        await policies.SyncAsync(true, CancellationToken.None);
        var job = Assert.Single(harness.Runtime.Jobs!.List());

        // Any other policy change starts a new revision; the finish-watched job keeps its owner.
        harness.ConfigureSettings(new PrefetchSettings { Enabled = true, FinishWatchedEnabled = true, CooldownMinutes = 30 });
        await policies.SyncAsync(true, CancellationToken.None);
        Assert.Equal("queued", harness.Runtime.Jobs.List().Single(row => row.Id == job.Id).State);

        harness.ConfigureSettings(new PrefetchSettings { Enabled = true, FinishWatchedEnabled = false, CooldownMinutes = 30 });
        await policies.SyncAsync(true, CancellationToken.None);
        Assert.Equal("cancelled", harness.Runtime.Jobs.List().Single(row => row.Id == job.Id).State);
        Assert.False(PlexPrefetchService.IsOwnerEnabled(PlexPrefetchService.FinishWatchedOwner,
            new PrefetchSettings { Enabled = false, FinishWatchedEnabled = true }, []));
    }

    private static long FinishWatchedMinimumBytes() => PlexPrefetchService.FinishWatchedMinimumBytes;

    private sealed class EmptyMetadataIndex : IPlexLibraryMetadataIndex
    {
        public PlexLibraryMetadataStatus Status { get; } = new(false, null, 0, null, false);
        public PlexLibraryMedia? Match(string? fileName) => null;
    }

    private sealed class ObservedBlobStore(IBlobStore inner, Action onRead) : IBlobStore
    {
        public Stream? ReadBlob(Guid id) { onRead(); return inner.ReadBlob(id); }
        public Task<T?> ReadBlob<T>(Guid id) { onRead(); return inner.ReadBlob<T>(id); }
        public Task WriteBlob(Guid id, Stream stream, CancellationToken ct = default) => inner.WriteBlob(id, stream, ct);
        public Task WriteBlob<T>(Guid id, T blob, CancellationToken ct = default) => inner.WriteBlob(id, blob, ct);
        public bool Exists(Guid id) => inner.Exists(id);
        public bool Delete(Guid id) => inner.Delete(id);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly string? _previousConfigPath;
        private readonly SqliteConnection _connection;
        private readonly FileBlobStore _blobs;
        private readonly RepairPatchStore _repairPatches;
        private readonly ServiceProvider _provider;
        private readonly ActiveReadRegistry _sessionReads;
        private readonly HttpClient _http = new(new PlexHandler());
        public required ConfigManager Config { get; init; }
        public required DbContextOptions<DavDatabaseContext> Options { get; init; }
        public required DavItem Item { get; init; }
        public required NativeCacheService Native { get; init; }
        public required PrefetchRuntime Runtime { get; init; }
        public required StreamingRepairScheduler Repairs { get; init; }

        private Harness(string root, string? previous, SqliteConnection connection, FileBlobStore blobs,
            RepairPatchStore repairs, ServiceProvider provider, ActiveReadRegistry reads)
        {
            _sessionReads = reads;
            _root = root;
            _previousConfigPath = previous;
            _connection = connection;
            _blobs = blobs;
            _repairPatches = repairs;
            _provider = provider;
        }

        public static async Task<Harness> CreateAsync(long fileSize, Func<Stream>? source = null, PrefetchSettings? settings = null, Action? onBlobRead = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "warm-outcome-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
            Directory.CreateDirectory(Path.Combine(root, "media"));
            Environment.SetEnvironmentVariable("CONFIG_PATH", root);
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
                new ConfigItem { ConfigName = ConfigKeys.NativeCacheMinFileMb, ConfigValue = "0" },
                new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[]
                    { new NativeCacheFolder { Id = "disk", Path = Path.Combine(root, "media"), MinFreeBytes = 0 } }) },
                new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = JsonSerializer.Serialize(settings ?? new PrefetchSettings()) },
            ]);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
            await using (var context = new DavDatabaseContext(options))
            {
                await context.Database.EnsureCreatedAsync();
            }
            var blobs = new FileBlobStore();
            var blobId = Guid.NewGuid();
            await blobs.WriteBlob(blobId, new DavNzbFile { Id = blobId, SegmentIds = ["source-" + blobId.ToString("N")] });
            var id = Guid.NewGuid();
            var item = new DavItem
            {
                Id = id,
                IdPrefix = id.ToString("N")[..5],
                Path = "/movie.mkv",
                Name = "movie.mkv",
                FileBlobId = blobId,
                FileSize = fileSize,
                Type = DavItem.ItemType.UsenetFile,
                SubType = DavItem.ItemSubType.NzbFile
            };
            await using (var context = new DavDatabaseContext(options))
            {
                context.Items.Add(item);
                await context.SaveChangesAsync();
            }
            var repairPatches = new RepairPatchStore(Path.Combine(root, "repairs"), 100);
            var native = new NativeCacheService(config, onBlobRead is null ? blobs : new ObservedBlobStore(blobs, onBlobRead), repairPatches);
            Assert.True(await native.WaitForInitializationAsync());
            var tracker = new StreamingFailureTracker();
            var repairs = new StreamingRepairScheduler(config, tracker, new ContextFactory(options));
            var services = new ServiceCollection();
            services.AddScoped(_ => new DavDatabaseContext(options));
            services.AddScoped(sp => new DavDatabaseClient(sp.GetRequiredService<DavDatabaseContext>(), blobs));
            services.AddSingleton<IDavContentStreamFactory>(new Factory(source ?? (() => new VerifiedSource(fileSize))));
            services.AddSingleton(repairs);
            var provider = services.BuildServiceProvider();
            var reads = new ActiveReadRegistry();
#pragma warning disable CA2000 // Owned by the harness and disposed with it.
            var runtime = new PrefetchRuntime(config, native, provider.GetRequiredService<IServiceScopeFactory>(), reads);
#pragma warning restore CA2000
            return new Harness(root, previous, connection, blobs, repairPatches, provider, reads)
            {
                Config = config,
                Options = options,
                Item = item,
                Native = native,
                Runtime = runtime,
                Repairs = repairs,
            };
        }

        /// <summary>Records a sustained five-minute playback session of the harness item.</summary>
        public void Watch(long servedBytes)
        {
            var session = _sessionReads.GetOrCreate(Item.Path, "player", Item.Name, Item.FileSize, null, null, null,
                DateTimeOffset.UtcNow.AddMinutes(-5));
            _sessionReads.Touch(session, servedBytes, servedBytes);
        }

        public PlexPrefetchService Policies() => new(Config, new PlexApiClient(_http, "warm-outcome-test"),
            Runtime, _provider.GetRequiredService<IServiceScopeFactory>(), _sessionReads);

        public void ConfigureSettings(PrefetchSettings settings) => Config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = JsonSerializer.Serialize(settings) }]);

        public Task ReplaceSourceAsync() => _blobs.WriteBlob(Item.FileBlobId!.Value,
            new DavNzbFile { Id = Item.FileBlobId.Value, SegmentIds = ["replacement"] });

        public void ConfigurePlexSource() => Config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.PlexServers, ConfigValue = JsonSerializer.Serialize(new[] { new PlexServer
                { Id = "machine", Name = "Plex", Url = "http://plex.test:32400", Token = "test-token", PathMappings = [new("/plex", "/")] } }) },
            new ConfigItem { ConfigName = ConfigKeys.SmartPrefetchSettings, ConfigValue = JsonSerializer.Serialize(new PrefetchSettings
                { Enabled = true, Sources = [new() { ServerId = "machine", Key = "/hubs/movies", Title = "Movies", Type = "movie" }] }) }
        ]);

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            _http.Dispose();
            await Native.DisposeAsync();
            await _provider.DisposeAsync();
            _repairPatches.Dispose();
            _blobs.Dispose();
            await _connection.DisposeAsync();
            Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
        }
    }

    private sealed class ContextFactory(DbContextOptions<DavDatabaseContext> options) : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() => new(options);
    }

    private sealed class Factory(Func<Stream> source) : IDavContentStreamFactory
    {
        public Task<Stream> OpenAsync(DavItem item, CancellationToken cancellationToken) => Task.FromResult(source());
    }

    private sealed class VerifiedSource(long length) : MemoryStream(new byte[length]), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
    }

    private sealed class UnverifiedSource() : MemoryStream(new byte[3]), ICacheReadEvidence
    {
        public bool LastReadCacheable => false;
    }

    private sealed class MissingArticleSource() : MemoryStream(new byte[3]), ICacheReadEvidence
    {
        public bool LastReadCacheable => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new UsenetArticleNotFoundException("foreign-segment");
    }

    private sealed class PlexHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath == "/status/sessions"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                    """<MediaContainer size="1" totalSize="1"><Video ratingKey="movie" type="movie" title="Movie"><Media selected="1"><Part selected="1" file="/plex/movie.mkv"/></Media></Video></MediaContainer>""")
                });
    }
}
