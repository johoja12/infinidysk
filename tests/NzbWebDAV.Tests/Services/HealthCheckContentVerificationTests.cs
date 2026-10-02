using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Clients.Usenet;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Health checks sample imported files' yEnc headers alongside STAT (#130): a message-id
/// that resolves to another upload's article passes STAT but is flagged, treated as missing
/// for PAR2 repair, and otherwise falls back to the existing replacement path. The one-time
/// sweep runs the same verification over imported files and queues repair for damage.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class HealthCheckContentVerificationTests : IAsyncLifetime
{
    private const int PartSize = 100;
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-health-content-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;
    private RepairPatchStore _patchStore = null!;
    private UsenetStreamingClient _usenet = null!;
    private QueueManager _queueManager = null!;
    private HealthCheckConnectionGate _gate = null!;

    public async Task InitializeAsync()
    {
        MismatchedArticleTracker.ResetForTests();
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={Path.Join(_configRoot, "db.sqlite")}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
            new ConfigItem
            {
                ConfigName = ConfigKeys.MediaLibraryDir,
                ConfigValue = Path.Join(_configRoot, "library"),
            },
        ]);
        Directory.CreateDirectory(Path.Join(_configRoot, "library"));

        _gate = new HealthCheckConnectionGate(_configManager);
        _patchStore = new RepairPatchStore(Path.Join(_configRoot, "patches"), 1024 * 1024);
        await _patchStore.EnsureCatalogLoadedAsync(CancellationToken.None);

        var websocketManager = new WebsocketManager();
        _usenet = new UsenetStreamingClient(
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        _queueManager = QueueManager.CreateForTests(
            _usenet,
            _configManager,
            websocketManager,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate(),
            healthCheckConnectionGate: _gate);
    }

    public async Task DisposeAsync()
    {
        MismatchedArticleTracker.ResetForTests();
        _queueManager.Dispose();
        _gate.Dispose();
        _usenet.Dispose();
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_configRoot))
                Directory.Delete(_configRoot, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task HealthyFile_StaysHealthy_WithBoundedHeaderReads()
    {
        var (ids, headers) = Release("ok", 2_000);
        var item = await AddVideoFileAsync("movie.mkv", ids);
        using var fake = ArticleContentVerifierTests.CreateClient(headers);
        var (service, par2) = await NewServiceAsync(fake);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Healthy, row.Result);
        Assert.Empty(par2.Requests);
        Assert.InRange(fake.BodyRequestCount, 1, ArticleContentSampleBudget.HealthCheck.TargetFor(2_000));
    }

    [Fact]
    public async Task ForeignArticle_PassesStatButIsRepairedFromPar2AsMissing()
    {
        var (ids, headers) = Release("sp", 100);
        headers[ids[^1]] = ArticleContentVerifierTests.ForeignHeader();
        var item = await AddVideoFileAsync("South.Park.S01E07.mkv", ids);
        using var fake = ArticleContentVerifierTests.CreateClient(headers);
        var (service, par2) = await NewServiceAsync(fake, Par2RepairOutcome.Repaired);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Equal([ids[^1]], Assert.Single(par2.Requests));
        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.RepairAction.RepairedViaPar2, row.RepairStatus);
        // The id belongs to another upload; it must not poison imports of that release.
        HealthCheckService.CheckCachedMissingSegmentIds([ids[^1]]);
    }

    [Fact]
    public async Task ForeignArticle_WithoutPar2Recovery_FallsBackToTheReplacementPath()
    {
        var (ids, headers) = Release("np", 100);
        headers[ids[0]] = ArticleContentVerifierTests.ForeignHeader();
        var item = await AddVideoFileAsync("movie.mkv", ids);
        using var fake = ArticleContentVerifierTests.CreateClient(headers);
        var (service, par2) = await NewServiceAsync(fake, Par2RepairOutcome.NotRepaired);

        await service.PerformHealthCheck(item, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Single(par2.Requests);
        var row = Assert.Single(GetHealthRows(item.Id));
        Assert.Equal(HealthCheckResult.HealthResult.Unhealthy, row.Result);
        Assert.Contains("File failed health validation.", row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueRepairForDamagedRelease_QueuesPriorityCheckThatConfirmsTheHintedArticle()
    {
        var (ids, headers) = Release("hint", 2_000);
        // A sparse collision between the light health-check samples.
        var hinted = ids[1_001];
        headers[hinted] = ArticleContentVerifierTests.ForeignHeader();
        var item = await AddVideoFileAsync("movie.mkv", ids);
        using var fake = ArticleContentVerifierTests.CreateClient(headers);
        var (service, par2) = await NewServiceAsync(fake, Par2RepairOutcome.Repaired);

        Assert.True(await service.QueueRepairForDamagedReleaseAsync(
            item.Id, [hinted], "warming found a foreign article", CancellationToken.None));

        var queued = ReloadItem(item.Id);
        Assert.True(queued.HealthRepairPending);
        Assert.Equal(HealthCheckService.ForcedRecheckSentinel, queued.NextHealthCheck);

        _context.ChangeTracker.Clear();
        var tracked = await _context.Items.SingleAsync(x => x.Id == item.Id);
        await service.PerformHealthCheck(tracked, _dbClient, concurrency: 4, CancellationToken.None);

        Assert.Equal([hinted], Assert.Single(par2.Requests));
        Assert.False(ReloadItem(item.Id).HealthRepairPending);
        Assert.Empty(service.PeekContentDamageHintsForTests(item.Id));
    }

    [Fact]
    public async Task QueueRepairForDamagedRelease_UnknownItem_ReturnsFalse()
    {
        using var fake = ArticleContentVerifierTests.CreateClient(new Dictionary<string, UsenetYencHeader>());
        var (service, _) = await NewServiceAsync(fake);

        Assert.False(await service.QueueRepairForDamagedReleaseAsync(
            Guid.NewGuid(), ["x@test"], "gone", CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_VerifiesOnlyImportedMediaInCategory_QueuesRepair_AndResumes()
    {
        var (goodIds, goodHeaders) = Release("good", 50);
        var (badIds, badHeaders) = Release("bad", 50);
        badHeaders[badIds[0]] = ArticleContentVerifierTests.ForeignHeader();
        var (otherIds, otherHeaders) = Release("other", 50);
        var category = await AddFolderAsync(DavItem.ContentFolder, "migration-plex");
        var good = await AddVideoFileAsync("A.Good.mkv", goodIds, category);
        var bad = await AddVideoFileAsync("B.Bad.mkv", badIds, category);
        var nfo = await AddVideoFileAsync("C.Release.nfo", Release("nfo", 1).Ids, category);
        var outside = await AddVideoFileAsync("D.Other.mkv", otherIds,
            await AddFolderAsync(DavItem.ContentFolder, "sonarr"));
        var headers = goodHeaders.Concat(badHeaders).Concat(otherHeaders)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        using var fake = ArticleContentVerifierTests.CreateClient(headers);
        var (service, _) = await NewServiceAsync(fake);
        var statePath = Path.Join(_configRoot, "sweep.json");
        var queued = new List<Guid>();
        using var sweep = NewSweep(service, statePath, queued);

        // A one-file limit pauses after the first imported media file.
        Assert.True(sweep.Start("migration-plex", limit: 1, restart: false));
        await sweep.RunTask.WaitAsync(TimeSpan.FromSeconds(30));
        var paused = sweep.GetState();
        Assert.Equal(ImportedContentSweepService.StatusPaused, paused.Status);
        Assert.Equal(1, paused.Checked);
        Assert.Equal(good.Path, paused.Cursor);

        // A fresh service instance resumes from the persisted cursor.
        using var resumed = NewSweep(service, statePath, queued);
        Assert.Equal(good.Path, resumed.GetState().Cursor);
        Assert.True(resumed.Start("migration-plex", limit: null, restart: false));
        await resumed.RunTask.WaitAsync(TimeSpan.FromSeconds(30));

        var done = resumed.GetState();
        Assert.Equal(ImportedContentSweepService.StatusCompleted, done.Status);
        Assert.Equal(2, done.Checked);
        Assert.Equal(1, done.Healthy);
        Assert.Equal(1, done.Damaged);
        Assert.Equal(1, done.Skipped);
        Assert.Equal([bad.Id], queued);
        Assert.Equal(bad.Path, Assert.Single(done.RecentFindings).Path);
        Assert.DoesNotContain(fake.RequestedSegmentIds, id => id.StartsWith("nfo-", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.RequestedSegmentIds, id => id.StartsWith("other-", StringComparison.Ordinal));
        Assert.NotEqual(nfo.Id, outside.Id);
    }

    private ImportedContentSweepService NewSweep(
        HealthCheckService service,
        string statePath,
        List<Guid> queued)
    {
        return new ImportedContentSweepService(service, _gate, dbContextFactory: null, statePath)
        {
            PauseBetweenFiles = TimeSpan.Zero,
            CreateDbContextOverride = () => new DavDatabaseContext(_options),
            QueueRepairOverride = (id, _, _, _) =>
            {
                lock (queued) queued.Add(id);
                return Task.FromResult(true);
            },
        };
    }

    private static (string[] Ids, Dictionary<string, UsenetYencHeader> Headers) Release(string prefix, int count)
    {
        var file = ArticleContentVerifierTests.PostedFile(prefix, count, out var headers);
        return (file.SegmentIds, headers);
    }

    private async Task<(HealthCheckService Service, ScriptedPar2RepairService Par2)> NewServiceAsync(
        INntpClient fake,
        Par2RepairOutcome par2Outcome = Par2RepairOutcome.NotRepaired)
    {
        await _usenet.ReplaceUnderlyingClientForTestsAsync(fake);
        var par2 = new ScriptedPar2RepairService(_configManager, _patchStore, par2Outcome);
        var service = new HealthCheckService(
            _configManager,
            _usenet,
            new WebsocketManager(),
            new BenchmarkGate(),
            new StreamingFailureTracker(),
            _queueManager,
            par2,
            _patchStore,
            new ArrReplacementSearchBudget(),
            _gate)
        {
            CreateDbContextOverride = () => new DavDatabaseContext(_options),
        };
        return (service, par2);
    }

    private async Task<DavItem> AddFolderAsync(DavItem parent, string name)
    {
        var folder = DavItem.New(
            Guid.NewGuid(), parent, name, fileSize: null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            releaseDate: null, lastHealthCheck: null, historyItemId: null, fileBlobId: null);
        _context.Items.Add(folder);
        await _context.SaveChangesAsync();
        return folder;
    }

    private async Task<DavItem> AddVideoFileAsync(string name, string[] segmentIds, DavItem? parent = null)
    {
        var itemId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await BlobStore.WriteBlob(blobId, new DavNzbFile
        {
            Id = itemId,
            SegmentIds = segmentIds,
            SegmentByteRanges = segmentIds
                .Select((_, index) => LongRange.FromStartAndSize((long)index * PartSize, PartSize))
                .ToArray(),
        });

        var item = DavItem.New(
            itemId,
            parent ?? DavItem.ContentFolder,
            name,
            fileSize: (long)segmentIds.Length * PartSize,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: null,
            fileBlobId: blobId);
        _context.Items.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    private List<HealthCheckResult> GetHealthRows(Guid itemId) =>
        _context.HealthCheckResults.AsNoTracking()
            .Where(x => x.DavItemId == itemId)
            .ToList();

    private DavItem ReloadItem(Guid itemId)
    {
        using var context = new DavDatabaseContext(_options);
        return context.Items.AsNoTracking().Single(x => x.Id == itemId);
    }

    private sealed class ScriptedPar2RepairService(
        ConfigManager configManager,
        RepairPatchStore store,
        Par2RepairOutcome repairOutcome) : Par2RepairService(configManager, null!, store)
    {
        public List<string[]> Requests { get; } = [];

        public override Task<Par2RepairOutcome> TryPar2RepairAsync(
            DavItem davItem, IReadOnlyList<string>? missingSegmentIds, CancellationToken ct)
        {
            Requests.Add(missingSegmentIds?.ToArray() ?? []);
            return Task.FromResult(repairOutcome);
        }
    }
}
