using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Clients;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Regrab;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.Services.Regrab;

public sealed class ArrRegrabServiceTests : IAsyncLifetime
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("regrab-service-").FullName;
    private string LibraryDir => Path.Join(_sandbox, "plex");
    private string LegacyMount => Path.Join(_sandbox, "nzbdav");
    private readonly string _dbPath = Path.Join(Path.GetTempPath(), $"nzbdav-regrab-{Guid.NewGuid():N}.sqlite");
    private ArrReplacementSearchBudget _budget = new();
    private FakeSonarr _sonarr = null!;
    private ConfigManager _config = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Join(LibraryDir, "TV-HD", "Show", "Season 1"));
        Directory.CreateDirectory(LegacyMount);
        _config = new ConfigManager();
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = LibraryDir },
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/infinidysk" },
        ]);
        _sonarr = new FakeSonarr(LibraryDir);
        await using var ctx = CreateContext();
        await ctx.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    private DavDatabaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        return new DavDatabaseContext(options);
    }

    private ArrRegrabService NewService(IReadOnlyList<ArrClient>? clients = null) =>
        new(_config, _budget, new ArrInstanceBackoff(),
            journal: new LibraryLinkRemovalJournal(Path.Join(_sandbox, "journal", "removals.jsonl")))
        {
            ContextFactory = CreateContext,
            ArrClientsOverride = () => clients ?? [_sonarr],
        };

    private string LegacyTarget(Guid legacyId)
    {
        var path = Path.Join(LegacyMount, ".ids", "a", "b", legacyId.ToString());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "legacy payload");
        return path;
    }

    private async Task<(DavItem Item, string LinkPath, string Target)> SeedLibraryItemAsync(
        string name = "Show - S01E07 - Episode WEBDL-2160p.mkv",
        Guid? arrDownloadId = null)
    {
        await using var ctx = CreateContext();
        var category = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, $"cat-{Guid.NewGuid():N}", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, null, null);
        var release = DavItem.New(Guid.NewGuid(), category, "Show.S01E07.2160p.WEB-DL-XEBEC", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory, null, null, null, null);
        var item = DavItem.New(Guid.NewGuid(), release, name, 100, DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile, DateTimeOffset.UtcNow, null, null, Guid.NewGuid());
        item.ArrDownloadId = arrDownloadId;
        ctx.Items.AddRange(category, release, item);
        var relative = Path.Join("TV-HD", "Show", "Season 1", name);
        var target = $"/mnt/infinidysk/.ids/1/2/3/4/5/{item.Id}";
        ctx.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(),
            DavItemId = item.Id,
            LinkPath = relative,
            TargetText = target,
            MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        var linkPath = Path.Join(LibraryDir, relative);
        File.CreateSymbolicLink(linkPath, target);
        return (item, linkPath, target);
    }

    private async Task<ArrRegrabRequest> SingleRowAsync()
    {
        await using var ctx = CreateContext();
        return await ctx.ArrRegrabRequests.AsNoTracking().SingleAsync();
    }

    // ------------------------------------------------------------- file modal

    [Fact]
    public async Task Request_RemovesSymlinkDeletesArrFileAndSearchesOnce_ThenIsIdempotent()
    {
        var (item, linkPath, target) = await SeedLibraryItemAsync();
        var service = NewService();

        var preview = await service.PreviewAsync(item.Id, null, CancellationToken.None);
        Assert.NotNull(preview);
        Assert.True(preview.Eligible);
        Assert.Equal("Sonarr", preview.Target!.App);
        Assert.Equal(0, _sonarr.RemoveCalls);

        var result = await service.RequestAsync(item.Id, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Accepted, result.Message);
        Assert.Equal(ArrRegrabStatus.Requested, result.Request!.Status);
        Assert.Equal(1, _sonarr.RemoveCalls);
        Assert.Equal(1, _sonarr.SearchCalls);
        Assert.Null(new FileInfo(linkPath).LinkTarget);
        Assert.False(File.Exists(linkPath));
        var row = await SingleRowAsync();
        Assert.True(row.LinkRemoved);
        Assert.Equal(target, row.PreviousLinkTarget);
        Assert.Equal(item.Id, row.DavItemId);
        var journal = service.Journal.Read();
        Assert.Equal(new[] { "intent", "removed" }, journal.Select(e => e.Event).ToArray());
        Assert.All(journal, e => Assert.Equal(target, e.PreviousTarget));
        Assert.Contains("Sonarr episode", journal[0].ArrTarget);

        var again = await service.RequestAsync(item.Id, null, CancellationToken.None);
        Assert.True(again!.Accepted);
        Assert.Contains("already requested", again.Message);
        Assert.Equal(1, _sonarr.RemoveCalls);
        Assert.Equal(1, _sonarr.SearchCalls);
        var stillOne = await service.PreviewAsync(item.Id, null, CancellationToken.None);
        Assert.False(stillOne!.Eligible);
        Assert.Equal(ArrRegrabStatus.Requested, stillOne.Request!.Status);
    }

    [Fact]
    public async Task Request_FromMigrationAutomation_RecordsItsSourceAndReason()
    {
        var (item, _, _) = await SeedLibraryItemAsync();
        var service = NewService();

        var result = await service.RequestAsync(item.Id, null, CancellationToken.None,
            ArrRegrabService.SourceMigration, "migration validation failed: bounded read timed out");

        Assert.True(result!.Accepted, result.Message);
        var row = await SingleRowAsync();
        Assert.Equal(ArrRegrabService.SourceMigration, row.Source);
        Assert.Equal("migration validation failed: bounded read timed out", row.Reason);
        Assert.Equal(1, _sonarr.RemoveCalls);
    }

    [Fact]
    public async Task Request_BlocklistsWhenTheDownloadIdIsKnown()
    {
        var downloadId = Guid.NewGuid();
        var (item, _, _) = await SeedLibraryItemAsync(arrDownloadId: downloadId);
        var service = NewService();

        var result = await service.RequestAsync(item.Id, null, CancellationToken.None);

        Assert.True(result!.Accepted, result.Message);
        Assert.Equal(downloadId, _sonarr.BlocklistedDownloadId);
        Assert.Equal(0, _sonarr.RemoveCalls);
        Assert.True((await SingleRowAsync()).Blocklisted);
    }

    [Fact]
    public async Task Request_IsRejectedWithoutAnArrMapping()
    {
        var (item, linkPath, _) = await SeedLibraryItemAsync();
        _sonarr.Matches = false;
        var service = NewService();

        var preview = await service.PreviewAsync(item.Id, null, CancellationToken.None);
        var result = await service.RequestAsync(item.Id, null, CancellationToken.None);

        Assert.False(preview!.Eligible);
        Assert.Contains("does not list a media file", preview.DisabledReason);
        Assert.False(result!.Accepted);
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        Assert.Equal(0, _sonarr.RemoveCalls);
        await using var ctx = CreateContext();
        Assert.Empty(await ctx.ArrRegrabRequests.ToListAsync());
    }

    [Fact]
    public async Task Request_IsRejectedWhenNoArrInstanceIsEnabled()
    {
        var (item, _, _) = await SeedLibraryItemAsync();
        var service = NewService(clients: []);

        var preview = await service.PreviewAsync(item.Id, null, CancellationToken.None);

        Assert.False(preview!.Eligible);
        Assert.Equal("No Sonarr or Radarr instance is enabled.", preview.DisabledReason);
    }

    [Fact]
    public async Task Request_RefusesRegularFileLibraryEntries()
    {
        var (item, linkPath, _) = await SeedLibraryItemAsync();
        File.Delete(linkPath);
        await File.WriteAllTextAsync(linkPath, "real media file");
        var service = NewService();

        var result = await service.RequestAsync(item.Id, null, CancellationToken.None);

        Assert.False(result!.Accepted);
        Assert.Contains("not a symlink", result.Message);
        Assert.True(File.Exists(linkPath));
        Assert.Equal(0, _sonarr.RemoveCalls);
    }

    [Fact]
    public async Task Request_DefersOnArrTimeoutAndNeverDeletesTwice()
    {
        var (item, linkPath, _) = await SeedLibraryItemAsync();
        _sonarr.RemoveFailure = new ArrRequestTimeoutException(
            "DELETE /api/v3/episodefile/7", _sonarr.Host, TimeSpan.FromSeconds(30), new TimeoutException());
        var service = NewService();

        var result = await service.RequestAsync(item.Id, null, CancellationToken.None);

        Assert.True(result!.Accepted);
        var pending = await SingleRowAsync();
        Assert.Equal(ArrRegrabStatus.Pending, pending.Status);
        Assert.True(pending.LinkRemoved);
        Assert.Contains("did not respond within 30 seconds", pending.LastError);
        Assert.NotNull(pending.NextAttemptAt);
        Assert.False(File.Exists(linkPath));

        // Sonarr applied the delete before timing out: the retry only searches.
        _sonarr.RemoveFailure = null;
        _sonarr.CurrentFileId = null;
        await service.ProcessAsync(pending.Id, CancellationToken.None);

        var done = await SingleRowAsync();
        Assert.Equal(ArrRegrabStatus.Requested, done.Status);
        Assert.Equal(1, _sonarr.RemoveCalls);
        Assert.Equal(1, _sonarr.SearchOnlyCalls);
    }

    [Fact]
    public async Task Request_WithholdsSearchWhenTheReplacementBudgetIsSpent()
    {
        var (item, _, _) = await SeedLibraryItemAsync();
        var arrConfig = _config.GetArrConfig();
        var key = $"{_sonarr.Host.TrimEnd('/').ToLowerInvariant()}|episode:301";
        while (_budget.TryReserve(key, arrConfig.EffectiveQueueReplacementSearchLimit(),
                   arrConfig.EffectiveQueueReplacementSearchWindow()))
        {
        }

        var result = await NewService().RequestAsync(item.Id, null, CancellationToken.None);

        Assert.True(result!.Accepted);
        Assert.Equal(ArrRegrabStatus.SearchWithheld, result.Request!.Status);
        Assert.Equal(1, _sonarr.RemoveCalls);
        Assert.Equal(0, _sonarr.SearchCalls);
    }

    [Fact]
    public async Task CheckReplacements_MarksRequestReplacedWhenArrHasANewFile()
    {
        var (item, _, _) = await SeedLibraryItemAsync();
        var service = NewService();
        await service.RequestAsync(item.Id, null, CancellationToken.None);
        await using (var ctx = CreateContext())
        {
            var row = await ctx.ArrRegrabRequests.SingleAsync();
            row.NextAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        _sonarr.CurrentFileId = 999;
        Assert.Equal(1, await service.CheckReplacementsAsync(10, CancellationToken.None));
        Assert.Equal(ArrRegrabStatus.Replaced, (await SingleRowAsync()).Status);
    }

    [Fact]
    public async Task RecordHealthRepair_ShowsTheSameRequestedState()
    {
        var (item, linkPath, _) = await SeedLibraryItemAsync();
        var service = NewService();

        await service.RecordHealthRepairAsync(item, linkPath, _sonarr.Host,
            new ArrMediaFileMatch(ArrMediaKind.Episode, 7, [301]), searchWithheld: false, CancellationToken.None);

        var row = await SingleRowAsync();
        Assert.Equal(ArrRegrabStatus.Requested, row.Status);
        Assert.Equal(ArrRegrabService.SourceHealthRepair, row.Source);
        Assert.True(row.Blocklisted);
        await using var ctx = CreateContext();
        var states = await service.GetActiveByKeysAsync(ctx, [item.Id], [], CancellationToken.None);
        Assert.Equal(ArrRegrabStatus.Requested, states[$"dav:{item.Id:D}"].Status);
    }

    // ------------------------------------------------------------- migration failures

    private (MigrationFailureRecord Failure, string LinkPath, string Target) LegacyFailure(
        string reason = "Missing articles: 1 important file(s) have missing segments across all providers",
        string name = "South Park - S19E01 - Stunning and Brave WEBDL-1080p.mkv")
    {
        var legacyId = Guid.NewGuid();
        var target = LegacyTarget(legacyId);
        var relative = $"TV-HD/Show/Season 1/{name}";
        var linkPath = Path.Join(LibraryDir, relative);
        File.CreateSymbolicLink(linkPath, target);
        return (new MigrationFailureRecord
        {
            LegacyDavItemId = legacyId.ToString(),
            LibraryRelativePath = relative,
            Reason = reason,
            SubmissionState = "failed",
            SourceReleaseId = "release-1",
            ReleaseName = "South.Park.S19E01.1080p.WEB-DL-AndreMor",
            BatchIndex = 27,
        }, linkPath, target);
    }

    [Fact]
    public async Task MigrationFailure_TriggersExactlyOneRegrabAndRecordsLedgerState()
    {
        var (failure, linkPath, target) = LegacyFailure();
        var ignored = LegacyFailure("Encrypted Rar archive has no password specified.", "Other - S01E01.mkv");
        var service = NewService();

        Assert.Equal(1, await service.EnqueueMigrationFailuresAsync([failure, ignored.Failure], CancellationToken.None));
        Assert.Equal(0, await service.EnqueueMigrationFailuresAsync([failure, ignored.Failure], CancellationToken.None));
        Assert.Equal(0, _sonarr.TotalCalls);

        Assert.True(await service.ProcessNextDueAsync(CancellationToken.None));
        Assert.False(await service.ProcessNextDueAsync(CancellationToken.None));

        Assert.Equal(1, _sonarr.RemoveCalls);
        Assert.Equal(1, _sonarr.SearchCalls);
        Assert.False(File.Exists(linkPath));
        Assert.Equal("legacy payload", await File.ReadAllTextAsync(target));
        Assert.NotNull(new FileInfo(ignored.LinkPath).LinkTarget);
        var states = await service.GetMigrationStatesAsync([Guid.Parse(failure.LegacyDavItemId!)], CancellationToken.None);
        var state = Assert.Single(states).Value;
        Assert.Equal(ArrRegrabStatus.Requested, state.Status);
        Assert.Equal("Sonarr", state.ArrApp);
        Assert.Equal("migration import failed: missing articles", state.Reason);
    }

    [Fact]
    public async Task MigrationFailure_DoesNotStallOnArrTimeouts()
    {
        var (failure, linkPath, _) = LegacyFailure("Release damaged on Usenet: article content mismatch");
        _sonarr.RootFailure = new ArrRequestTimeoutException(
            "GET /api/v3/rootfolder", _sonarr.Host, TimeSpan.FromSeconds(30), new TimeoutException());
        var service = NewService();

        // Recording the failure for the batch ledger never touches Arr.
        Assert.Equal(1, await service.EnqueueMigrationFailuresAsync([failure], CancellationToken.None));
        Assert.True(await service.ProcessNextDueAsync(CancellationToken.None));

        var row = await SingleRowAsync();
        Assert.Equal(ArrRegrabStatus.Pending, row.Status);
        Assert.Contains("did not respond", row.LastError);
        Assert.True(row.NextAttemptAt > DateTime.UtcNow);
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        Assert.Equal(0, _sonarr.RemoveCalls);
        Assert.False(await service.ProcessNextDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MigrationFailure_SkipsChangedLinksWithoutTouchingArr()
    {
        var (failure, linkPath, _) = LegacyFailure();
        File.Delete(linkPath);
        File.CreateSymbolicLink(linkPath, "/mnt/infinidysk/.ids/9/9/9/9/9/" + Guid.NewGuid());
        var service = NewService();

        await service.EnqueueMigrationFailuresAsync([failure], CancellationToken.None);

        var row = await SingleRowAsync();
        Assert.Equal(ArrRegrabStatus.Skipped, row.Status);
        Assert.Contains("points somewhere else", row.LastError);
        Assert.False(await service.ProcessNextDueAsync(CancellationToken.None));
        Assert.Equal(0, _sonarr.TotalCalls);
        Assert.NotNull(new FileInfo(linkPath).LinkTarget);
    }

    [Fact]
    public async Task FailedMigrationImports_RequireADryRunAndRespectTheLimit()
    {
        var first = LegacyFailure(name: "A - S01E01.mkv");
        var second = LegacyFailure(name: "A - S01E02.mkv");
        var notEligible = LegacyFailure("Encrypted Rar archive has no password specified.", "A - S01E03.mkv");
        var missing = LegacyFailure(name: "A - S01E04.mkv");
        File.Delete(missing.LinkPath);
        var failures = new[] { first.Failure, second.Failure, notEligible.Failure, missing.Failure };
        var service = NewService();

        var rejected = await service.QueueMigrationFailuresAsync(failures, 1, "not-a-token", CancellationToken.None);
        Assert.False(rejected.Ok);

        var preview = await service.PreviewMigrationFailuresAsync(failures, CancellationToken.None);
        Assert.Equal(4, preview.Total);
        Assert.Equal(2, preview.Eligible);
        Assert.Equal(1, preview.Skipped[MigrationRegrabOutcome.NotEligibleReason]);
        Assert.Equal(1, preview.Skipped[MigrationRegrabOutcome.SourceLinkMissing]);
        Assert.Equal(0, _sonarr.TotalCalls);
        await using (var ctx = CreateContext())
            Assert.Empty(await ctx.ArrRegrabRequests.ToListAsync());

        var run = await service.QueueMigrationFailuresAsync(failures, 1, preview.PreviewToken, CancellationToken.None);
        Assert.True(run.Ok, run.Message);
        Assert.Equal(1, run.Queued);
        Assert.Equal(1, run.Remaining);

        var reused = await service.QueueMigrationFailuresAsync(failures, 1, preview.PreviewToken, CancellationToken.None);
        Assert.False(reused.Ok);

        var next = await service.PreviewMigrationFailuresAsync(failures, CancellationToken.None);
        Assert.Equal(1, next.Eligible);
        Assert.Equal(1, next.AlreadyRequested);
    }

    [Fact]
    public void MigrationFailureFeed_MapsFailedReleasesToTheirSelectedLibraryLinks()
    {
        var failedLeaf = Guid.NewGuid();
        var unselectedLeaf = Guid.NewGuid();
        var otherLeaf = Guid.NewGuid();
        NzbDavExportLeaf Leaf(Guid id, string release) =>
            new(id, $"/content/{id}.mkv", 100, release, null, null, "nzb", null, "ready", null);
        var manifest = new NzbDavExportManifest(
            2, "pkg", DateTimeOffset.UtcNow, "nzbdav",
            [
                new NzbDavExportRelease("r1", null, "payloads/r1.nzb", [Leaf(failedLeaf, "r1"), Leaf(unselectedLeaf, "r1")],
                    SourceJobName: "South.Park.S19E01.1080p.WEB-DL-AndreMor"),
                new NzbDavExportRelease("r2", null, "payloads/r2.nzb", [Leaf(otherLeaf, "r2")]),
            ],
            [
                new NzbDavSelectedLibraryLink("TV-HD/South Park/S19E01.mkv", "/mnt/remote/nzbdav/.ids/x/" + failedLeaf, failedLeaf),
                new NzbDavSelectedLibraryLink("TV-HD/Other/S01E01.mkv", "/mnt/remote/nzbdav/.ids/y/" + otherLeaf, otherLeaf),
            ],
            [], [], BatchIndex: 27);
        var package = new NzbWebDAV.UsenetMigration.Source.NzbDavVerifiedPackage(
            "/config/migration-input/batch", manifest, "digest", new Dictionary<string, string>());

        var records = MigrationFailureFeed.Build(package, [("nzbdav:r1", "Missing articles: 1 important file(s)")]);

        var record = Assert.Single(records);
        Assert.Equal(failedLeaf.ToString("D"), record.LegacyDavItemId);
        Assert.Equal("TV-HD/South Park/S19E01.mkv", record.LibraryRelativePath);
        Assert.Equal("/mnt/remote/nzbdav/.ids/x/" + failedLeaf, record.OriginalTarget);
        Assert.Equal("South.Park.S19E01.1080p.WEB-DL-AndreMor", record.ReleaseName);
        Assert.Equal(27, record.BatchIndex);
        Assert.Equal("failed", record.SubmissionState);
    }

    private sealed class FakeSonarr(string root) : SonarrClient("http://sonarr.test:8989", "test-key")
    {
        public bool Matches { get; set; } = true;
        public Exception? RootFailure { get; set; }
        public Exception? RemoveFailure { get; set; }
        public int? CurrentFileId { get; set; } = 7;
        public int RemoveCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public int SearchOnlyCalls { get; private set; }
        public int TotalCalls { get; private set; }
        public Guid? BlocklistedDownloadId { get; private set; }

        public override Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct = default)
        {
            TotalCalls++;
            return RootFailure is not null
                ? Task.FromException<List<ArrRootFolder>>(RootFailure)
                : Task.FromResult(new List<ArrRootFolder> { new() { Path = root } });
        }

        public override Task<ArrMediaFileMatch?> FindMediaFileAsync(string symlinkOrStrmPath, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(Matches ? new ArrMediaFileMatch(ArrMediaKind.Episode, 7, [301]) : null);
        }

        public override Task<ArrHistory> GetMediaImportHistoryAsync(
            ArrMediaFileMatch mediaFile, int page, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new ArrHistory());

        public override Task<ArrMissingPayloadCleanupOutcome> RemoveMissingPayloadAndSearchAsync(
            ArrMediaFileMatch match,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default)
        {
            TotalCalls++;
            RemoveCalls++;
            if (RemoveFailure is not null)
                return Task.FromException<ArrMissingPayloadCleanupOutcome>(RemoveFailure);
            if (shouldRequestSearch is not null && !shouldRequestSearch(match.MediaKeys))
                return Task.FromResult(ArrMissingPayloadCleanupOutcome.RemovedSearchWithheld);
            SearchCalls++;
            return Task.FromResult(ArrMissingPayloadCleanupOutcome.RemovedSearchRequested);
        }

        public override Task<ArrRepairOutcome> RemoveAndBlocklist(
            ArrMediaFileMatch mediaFile,
            Guid downloadId,
            Func<IReadOnlyList<string>, bool>? shouldRequestSearch = null,
            CancellationToken ct = default)
        {
            TotalCalls++;
            BlocklistedDownloadId = downloadId;
            SearchCalls++;
            return Task.FromResult(ArrRepairOutcome.RemoveAndBlocklistSucceeded);
        }

        public override Task<int?> GetCurrentMediaFileIdAsync(ArrMediaFileMatch match, CancellationToken ct = default)
        {
            TotalCalls++;
            return Task.FromResult(CurrentFileId);
        }

        public override Task RequestSearchAsync(ArrMediaFileMatch match, CancellationToken ct = default)
        {
            TotalCalls++;
            SearchOnlyCalls++;
            return Task.CompletedTask;
        }
    }
}
