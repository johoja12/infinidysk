using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbDavMigration.Export;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetMigration;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Canary;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

[Collection(nameof(ConfigPathCollection))]
public sealed class NzbDavMigrationControllerTests : IAsyncLifetime
{
    private readonly string _config = Path.Join(Path.GetTempPath(), $"nzbdav-api-{Guid.NewGuid():N}");
    private string? _previousConfig;
    private string? _previousApiKey;

    [Fact]
    public async Task Connect_RequiresVerifiedPackageBeneathMigrationInputAndUsesSafeDefaults()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var package = await CreatePackageAsync("inside", 2);
        var controller = CreateController(harness);

        var result = await controller.Connect(new NzbDavConnectRequest(package, null, null));

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal(2, json.RootElement.GetProperty("selectionCount").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("exclusionCount").GetInt32());
        Assert.Equal(64, json.RootElement.GetProperty("packageDigest").GetString()!.Length);
        var session = await harness.Store.GetSessionAsync();
        Assert.Equal(MigrationSourceTypes.NzbDav, session.SourceType);
        Assert.Equal(5, session.MaxQueueDepth);
        Assert.Equal(1, session.SubmitWorkers);
        var mapped = await controller.PutCategories(new CategoryMapRequest(
            [new CategoryMapEntry("Migration-TV", "nzbdav-canary-tv", "migrate")]));
        Assert.IsType<OkObjectResult>(mapped);
        Assert.Equal("nzbdav-canary-tv", Assert.Single(await harness.Store.GetCategoryMapAsync()).TargetCategory);

        var outside = await CreatePackageAsync("outside", 1, beneathInput: false);
        var rejected = await controller.Connect(new NzbDavConnectRequest(outside, 5, 1));
        Assert.IsType<BadRequestObjectResult>(rejected);
    }

    [Fact]
    public async Task ScanAndRun_UseLegalTransitionsAndRequireDigestConfirmation()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var packagePath = await CreatePackageAsync("run", 1);
        var controller = CreateController(harness);
        var connected = Assert.IsType<OkObjectResult>(
            await controller.Connect(new NzbDavConnectRequest(packagePath, 5, 1)));
        using var connectedJson = JsonDocument.Parse(JsonSerializer.Serialize(connected.Value));
        var digest = connectedJson.RootElement.GetProperty("packageDigest").GetString()!;

        Assert.IsType<OkObjectResult>(await controller.StartScan());
        Assert.IsType<BadRequestObjectResult>(await controller.StartRun(new NzbDavRunRequest(digest, 1)));
        Assert.IsType<OkObjectResult>(await controller.CancelScan());
        await harness.Store.UpdateSessionAsync(session => session.Status = "scanned");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", Verdict = "green", VerdictReasons = "[]",
                Included = true, ScannedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.IsType<BadRequestObjectResult>(
            await controller.StartRun(new NzbDavRunRequest(new string('0', 64), 1)));
        var started = Assert.IsType<OkObjectResult>(
            await controller.StartRun(new NzbDavRunRequest(digest, 1)));
        Assert.Equal("running", (await harness.Store.GetSessionAsync()).Status);
        await using var verify = harness.Mig();
        Assert.Equal(MigrationSourceTypes.NzbDav, (await verify.MigrationRuns.SingleAsync()).SourceType);
        Assert.NotNull(started.Value);
        Assert.IsType<OkObjectResult>(await controller.StopRun());
        Assert.Equal("paused", (await harness.Store.GetSessionAsync()).Status);
        Assert.IsType<OkObjectResult>(await controller.ResumeRun());
        Assert.Equal("running", (await harness.Store.GetSessionAsync()).Status);
        Assert.IsType<OkObjectResult>(await controller.StopRun(cancel: true));
        Assert.Equal("cancelling", (await harness.Store.GetSessionAsync()).Status);
    }

    [Fact]
    public async Task Correlation_RequiresTerminalReconciliationAndReturnsEverySelectedLeaf()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var packagePath = await CreatePackageAsync("report", 2);
        var controller = CreateController(harness);
        await controller.Connect(new NzbDavConnectRequest(packagePath, 5, 1));

        Assert.IsType<BadRequestObjectResult>(await controller.GetCorrelation());
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            var package = await new NzbDavPackageReader().ReadAsync(packagePath);
            var first = package.Manifest.SelectedLinks[0];
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]", ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "release-1", MetaPath = "package", VirtualPath = "a.mkv", FileName = "a.mkv",
                NormalisedName = "a.mkv", SourceFileId = first.LegacyDavItemId.ToString(), FileSize = 10,
                FileStatus = "ambiguous", Flags = "{\"candidates\":[\"one\",\"two\"]}",
            });
            await db.SaveChangesAsync();
        }

        var report = Assert.IsType<OkObjectResult>(await controller.GetCorrelation());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report.Value));
        Assert.Equal(2, json.RootElement.GetProperty("selectedCount").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(1, json.RootElement.GetProperty("ambiguityCount").GetInt32());
        Assert.IsType<BadRequestObjectResult>(await controller.GenerateCanaryPlan());
    }

    [Fact]
    public async Task Reconcile_RejectsNonTerminalSession()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var packagePath = await CreatePackageAsync("reconcile-active", 1);
        var controller = CreateController(harness);
        await controller.Connect(new NzbDavConnectRequest(packagePath, 5, 1));

        Assert.IsType<BadRequestObjectResult>(await controller.Reconcile());
    }

    [Fact]
    public async Task CanaryPlan_RejectsIncompleteExactCorrelationWithoutAmbiguity()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var packagePath = await CreatePackageAsync("incomplete-plan", 2);
        var package = await new NzbDavPackageReader().ReadAsync(packagePath);
        var selected = package.Manifest.SelectedLinks[0];
        var leaf = package.Manifest.Releases.SelectMany(release => release.Leaves).First();
        var targetId = Guid.NewGuid();
        var controller = CreateController(harness);
        await controller.Connect(new NzbDavConnectRequest(packagePath, 5, 1));
        await using (var db = harness.Mig())
        {
            var now = DateTime.UtcNow;
            var run = new MigrationRun
            {
                SourceType = MigrationSourceTypes.NzbDav, Status = "complete", StartedAt = now, CompletedAt = now,
            };
            db.MigrationRuns.Add(run);
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]", ScannedAt = now,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "release-1", MetaPath = "package", VirtualPath = leaf.LegacyPath,
                FileName = "0.mkv", NormalisedName = "0.mkv", FileSize = leaf.FileSize,
                SourceFileId = selected.LegacyDavItemId.ToString(), FileStatus = "exact",
                Flags = "{\"match\":\"article-identity\"}", NewDavItemId = targetId.ToString(),
            });
            var migratedRelease = new MigratedRelease
            {
                SourceType = MigrationSourceTypes.NzbDav, SourceReleaseId = "release-1",
                FirstRunId = 0, LastRunId = 0, ExpectedFileCount = 2, MappedFileCount = 1,
                MigratedAt = now, LastVerifiedAt = now,
            };
            db.MigratedReleases.Add(migratedRelease);
            await db.SaveChangesAsync();
            migratedRelease.FirstRunId = run.Id;
            migratedRelease.LastRunId = run.Id;
            db.MigratedFiles.Add(new MigratedFile
            {
                MigratedReleaseId = migratedRelease.Id, VirtualPath = leaf.LegacyPath,
                NormalisedRelativePath = "0.mkv", NormalisedName = "0.mkv", FileSize = leaf.FileSize,
                DavItemId = targetId, SourceFileId = selected.LegacyDavItemId.ToString(),
                MatchMethod = "article-identity", LastVerifiedAt = now,
            });
            await db.SaveChangesAsync();
            await harness.Store.UpdateSessionAsync(session =>
            {
                session.Status = "complete";
                session.CurrentRunId = run.Id;
            });
        }

        Assert.IsType<BadRequestObjectResult>(await controller.GenerateCanaryPlan());
    }

    [Fact]
    public async Task CanaryPlan_GeneratesAndDownloadsOnlyForExactTerminalCorrelation()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var packagePath = await CreatePackageAsync("plan", 1);
        var package = await new NzbDavPackageReader().ReadAsync(packagePath);
        var selected = Assert.Single(package.Manifest.SelectedLinks);
        var leaf = Assert.Single(package.Manifest.Releases.SelectMany(release => release.Leaves));
        var targetId = Guid.NewGuid();
        var controller = CreateController(harness);
        await controller.Connect(new NzbDavConnectRequest(packagePath, 5, 1));
        await using (var db = harness.Mig())
        {
            var now = DateTime.UtcNow;
            var run = new MigrationRun
            {
                SourceType = MigrationSourceTypes.NzbDav, Status = "complete", StartedAt = now, CompletedAt = now,
            };
            db.MigrationRuns.Add(run);
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]", ScannedAt = now,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "release-1", MetaPath = "package", VirtualPath = leaf.LegacyPath,
                FileName = "0.mkv", NormalisedName = "0.mkv", FileSize = leaf.FileSize,
                SourceFileId = selected.LegacyDavItemId.ToString(), FileStatus = "exact",
                Flags = "{\"match\":\"article-identity\"}", NewDavItemId = targetId.ToString(),
            });
            var migratedRelease = new MigratedRelease
            {
                SourceType = MigrationSourceTypes.NzbDav, SourceReleaseId = "release-1",
                FirstRunId = 0, LastRunId = 0, ExpectedFileCount = 1, MappedFileCount = 1,
                MigratedAt = now, LastVerifiedAt = now,
            };
            db.MigratedReleases.Add(migratedRelease);
            await db.SaveChangesAsync();
            migratedRelease.FirstRunId = run.Id;
            migratedRelease.LastRunId = run.Id;
            db.MigratedFiles.Add(new MigratedFile
            {
                MigratedReleaseId = migratedRelease.Id, VirtualPath = leaf.LegacyPath,
                NormalisedRelativePath = "0.mkv", NormalisedName = "0.mkv", FileSize = leaf.FileSize,
                DavItemId = targetId, SourceFileId = selected.LegacyDavItemId.ToString(),
                MatchMethod = "article-identity", LastVerifiedAt = now,
            });
            await db.SaveChangesAsync();
            await harness.Store.UpdateSessionAsync(session =>
            {
                session.Status = "complete";
                session.CurrentRunId = run.Id;
            });
        }

        Assert.IsType<OkObjectResult>(await controller.GenerateCanaryPlan());
        var download = Assert.IsType<FileContentResult>(await controller.DownloadCanaryPlan());
        Assert.NotEmpty(download.FileContents);
        Assert.Equal("application/zip", download.ContentType);
        using var archive = new System.IO.Compression.ZipArchive(
            new MemoryStream(download.FileContents),
            System.IO.Compression.ZipArchiveMode.Read);
        Assert.Equal(
            ["plan.json", "SHA256SUMS"],
            archive.Entries.Select(entry => entry.FullName).Order().ToArray());
    }

    [Fact]
    public async Task FullConnect_IsIdempotentAndFencesOutOfOrderOrUnacknowledgedBatches()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('d', 64);
        var first = await CreateFullPackageAsync("full-0", masterDigest, 0, 2);
        var second = await CreateFullPackageAsync("full-1", masterDigest, 1, 2);
        var controller = CreateController(harness);
        var request0 = new NzbDavFullConnectRequest(first, masterDigest, 100, 95, 5, 1);

        Assert.IsType<OkObjectResult>(await controller.ConnectFull(request0));
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(request0));
        await using (var db = harness.Mig())
            Assert.Single(await db.NzbDavBatches.ToListAsync());
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(second, masterDigest, 100, 95, 5, 1)));

        await using (var db = harness.Mig())
        {
            var batch = await db.NzbDavBatches.SingleAsync();
            batch.Status = "acknowledged";
            await db.SaveChangesAsync();
        }
        await harness.Store.UpdateSessionAsync(session => session.Status = "connected");
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(second, masterDigest, 100, 95, 5, 1)));
        var status = Assert.IsType<OkObjectResult>(await controller.GetFullStatus());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status.Value));
        Assert.Equal(2, json.RootElement.GetProperty("batchCount").GetInt32());
        Assert.Equal(95, json.RootElement.GetProperty("recoverableCount").GetInt32());
    }

    [Fact]
    public async Task FullConnect_ExplicitPriorityOrderPreservesActiveAndPackageGuards()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var digest = new string('e', 64);
        var first = await CreateFullPackageAsync("priority-0", digest, 0, 3);
        var last = await CreateFullPackageAsync("priority-2", digest, 2, 3);
        var middle = await CreateFullPackageAsync("priority-1", digest, 1, 3);
        var changed = await CreateFullPackageAsync("priority-2-changed", digest, 2, 3);
        var controller = CreateController(harness);
        var priority = new NzbDavFullConnectRequest(last, digest, 100, 95, 5, 1, true);
        // Reordering is for remaining batches of an established master.
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority));
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(first, digest, 100, 95, 5, 1)));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority));
        await using (var db = harness.Mig())
        {
            var batch = await db.NzbDavBatches.SingleAsync();
            batch.Status = "acknowledged";
            await db.SaveChangesAsync();
        }
        await harness.Store.UpdateSessionAsync(session => session.Status = "connected");
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority with { AllowOutOfOrder = false }));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority with { RecoverableCount = 94 }));
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(priority));
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(priority));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority with { PackagePath = changed }));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(priority with { PackagePath = middle }));
        await using (var db = harness.Mig())
        {
            var batch = await db.NzbDavBatches.SingleAsync(item => item.BatchIndex == 2);
            batch.Status = "acknowledged";
            await db.SaveChangesAsync();
        }
        await harness.Store.UpdateSessionAsync(session => session.Status = "connected");
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(priority with { PackagePath = middle }));
        await using (var db = harness.Mig())
            Assert.Equal(new[] { 0, 1, 2 }, await db.NzbDavBatches.OrderBy(item => item.BatchIndex)
                .Select(item => item.BatchIndex).ToArrayAsync());
    }

    [Fact]
    public async Task FullConnect_AllowsSecondRootMasterAfterFirstIsAcknowledged()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var specialDigest = new string('a', 64);
        var plexDigest = new string('b', 64);
        var special = await CreateFullPackageAsync("special-root", specialDigest, 0, 1);
        var plex = await CreateFullPackageAsync("plex-root", plexDigest, 0, 1);
        var controller = CreateController(harness);

        Assert.IsType<OkObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(special, specialDigest, 10, 1, 5, 1)));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(plex, plexDigest, 100, 95, 5, 1)));

        await using (var db = harness.Mig())
        {
            var batch = await db.NzbDavBatches.SingleAsync();
            batch.Status = "acknowledged";
            await db.SaveChangesAsync();
        }
        await harness.Store.UpdateSessionAsync(session => session.Status = "connected");
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(
            new NzbDavFullConnectRequest(plex, plexDigest, 100, 95, 5, 1)));
        await using (var db = harness.Mig())
            Assert.Equal(2, await db.NzbDavMasters.CountAsync());
        var status = Assert.IsType<OkObjectResult>(await controller.GetFullStatus());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status.Value));
        Assert.Equal(100, json.RootElement.GetProperty("sourceLinkCount").GetInt32());
    }

    [Theory]
    [InlineData("validated")]
    [InlineData("unreadable")]
    [InlineData("missing")]
    [InlineData("replaced")]
    public async Task Pipeline_CheckpointSurvivesNextScanAndAcknowledgesOldBatchDuringImport(string outcome)
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var master = new string('e', 64);
        var first = await CreateFullPackageAsync("pipeline-0", master, 0, 3);
        var second = await CreateFullPackageAsync("pipeline-1", master, 1, 3, libraryPath: "Migration-TV/b.mkv");
        var third = await CreateFullPackageAsync("pipeline-2", master, 2, 3, libraryPath: "Migration-TV/c.mkv");
        var overlapping = await CreateFullPackageAsync("pipeline-overlap", master, 1, 3);
        var controller = CreateController(harness);
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(new(first, master, 3, 3, 10, 2)));
        var package = await new NzbDavPackageReader().ReadAsync(first);
        var selected = Assert.Single(package.Manifest.SelectedLinks);
        var duplicateSource = await CreateFullPackageAsync("pipeline-duplicate-id", master, 1, 3,
            libraryPath: "Migration-TV/other.mkv", sourceId: selected.LegacyDavItemId);
        var run = await harness.Store.BeginRunAsync();
        await harness.Store.AttachNzbDavBatchRunAsync(package.PackageDigest, run);
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "proof",
                StoreBasename = "proof",
                SubmitFileName = "proof.nzb",
                QueueFileName = "proof.nzb",
                JobName = "proof",
                VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "proof",
                MetaPath = "payload",
                VirtualPath = "/content/a.mkv",
                FileName = "a.mkv",
                NormalisedName = "a.mkv",
                SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "exact",
                NewDavItemId = Guid.NewGuid().ToString()
            });
            db.Submissions.Add(new MigrationSubmission { StoreRef = "proof", State = "processing", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var plan = new NzbDavCanaryPlan(1, run, package.PackageDigest, DateTimeOffset.UtcNow, 1, 1, true,
            [new(selected.LibraryRelativePath, selected.OriginalTarget, selected.LegacyDavItemId,
                10, "exact", "proof", ".ids/target", "planned")]);
        var directory = await new NzbDavCanaryPlanWriter().WriteAsync(Path.Join(_config, "migration-output", "nzbdav"), plan);
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Join(directory, "plan.json")))).ToLowerInvariant();
        Assert.IsType<BadRequestObjectResult>(await controller.CheckpointValidation(0, new(digest)));
        await harness.Store.UpdateSessionAsync(item => item.Status = "complete");
        Assert.IsType<BadRequestObjectResult>(await controller.CheckpointValidation(0, new(digest)));
        await using (var db = harness.Mig())
        {
            (await db.Submissions.SingleAsync()).State = "completed";
            await db.SaveChangesAsync();
        }
        await using (var db = harness.Mig())
        {
            (await db.NzbDavBatches.SingleAsync()).Status = "acknowledged";
            await db.SaveChangesAsync();
        }
        Assert.IsType<BadRequestObjectResult>(await controller.CheckpointValidation(0, new(digest)));
        await using (var db = harness.Mig())
        {
            (await db.NzbDavBatches.SingleAsync()).Status = "running";
            await db.SaveChangesAsync();
        }
        Assert.IsType<BadRequestObjectResult>(await controller.CheckpointValidation(0, new(new string('0', 64))));
        Assert.IsType<OkObjectResult>(await controller.CheckpointValidation(0, new(digest)));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(new(overlapping, master, 3, 3, 10, 2)));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(new(duplicateSource, master, 3, 3, 10, 2)));
        Assert.IsType<OkObjectResult>(await controller.ConnectFull(new(second, master, 3, 3, 10, 2)));
        Assert.IsType<BadRequestObjectResult>(await controller.ConnectFull(new(third, master, 3, 3, 10, 2)));
        // A real next scan removes the singleton release/submission proof. The frozen
        // package and exact IDs must remain sufficient across controller restart.
        await using (var db = harness.Mig())
            await UsenetMigrationStore.ClearScanArtifactsAsync(db, CancellationToken.None);
        await harness.Store.UpdateSessionAsync(item => item.Status = "running");
        controller = CreateController(harness);
        Assert.IsType<OkObjectResult>(await controller.CheckpointValidation(0, new(digest)));
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0, new(new string('0', 64), 1, 1)));
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0, new(digest, 1, 0)));
        var sourceIds = new[] { selected.LegacyDavItemId.ToString() };
        var acknowledgement = new NzbDavBatchPlanAcknowledgementRequest(digest,
            outcome is "missing" or "replaced" ? 0 : 1, outcome == "validated" ? 1 : 0,
            outcome == "unreadable" ? sourceIds : null, outcome == "missing" ? sourceIds : null,
            outcome == "replaced" ? sourceIds : null);
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0, acknowledgement));
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0, acknowledgement));
        Assert.Equal("running", (await harness.Store.GetSessionAsync()).Status);
        await using var verify = harness.Mig();
        Assert.Equal("acknowledged", (await verify.NzbDavBatches.SingleAsync(item => item.BatchIndex == 0)).Status);
        Assert.Equal("connected", (await verify.NzbDavBatches.SingleAsync(item => item.BatchIndex == 1)).Status);
    }

    [Fact]
    public async Task AcknowledgePlan_RequiresTerminalExactFullyAppliedBatch()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("ack", masterDigest, 0, 1);
        var package = await new NzbDavPackageReader().ReadAsync(packagePath);
        var selected = Assert.Single(package.Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "active", StoreBasename = "active", SubmitFileName = "active.nzb",
                QueueFileName = "active.nzb", JobName = "active", VerdictReasons = "[]", ScannedAt = DateTime.UtcNow,
            });
            db.Submissions.Add(new MigrationSubmission
                { StoreRef = "active", State = "processing", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var acknowledgement = new NzbDavBatchPlanAcknowledgementRequest(new string('f', 64), 1, 1);
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0, acknowledgement));

        await using (var db = harness.Mig())
        {
            db.Submissions.RemoveRange(db.Submissions);
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "nzbdav:release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]", ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "nzbdav:release-1", MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "exact", NewDavItemId = Guid.NewGuid().ToString(),
            });
            db.Submissions.Add(new MigrationSubmission
                { StoreRef = "nzbdav:release-1", State = "completed", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0, acknowledgement));
        await using var verify = harness.Mig();
        var batch = await verify.NzbDavBatches.SingleAsync();
        Assert.Equal("acknowledged", batch.Status);
        Assert.Equal(1, batch.AppliedCount);
        Assert.Equal(1, batch.ValidatedCount);
    }

    [Fact]
    public async Task AcknowledgePlan_AllowsRecordedTerminalImportFailureWithoutAnUnsafeLink()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("failed-ack", masterDigest, 0, 1);
        var selected = Assert.Single((await new NzbDavPackageReader().ReadAsync(packagePath))
            .Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "nzbdav:release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "nzbdav:release-1", MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "import-failed",
            });
            db.Submissions.Add(new MigrationSubmission
            {
                StoreRef = "nzbdav:release-1", State = "failed", Error = "RAR signature not found",
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var failures = Assert.IsType<OkObjectResult>(await controller.GetImportFailures());
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(failures.Value));
        Assert.Equal(1, result.RootElement.GetProperty("failedCount").GetInt32());
        Assert.Equal(selected.LegacyDavItemId.ToString(), result.RootElement.GetProperty("failures")[0]
            .GetProperty("LegacyDavItemId").GetString());
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(new string('f', 64), 0, 0)));
        await using var verify = harness.Mig();
        Assert.Equal("acknowledged", (await verify.NzbDavBatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task AcknowledgePlan_RecordsOneExplicitUnreadableExactLink()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("unreadable-ack", masterDigest, 0, 1);
        var selected = Assert.Single((await new NzbDavPackageReader().ReadAsync(packagePath))
            .Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "nzbdav:release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "nzbdav:release-1", MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "exact", NewDavItemId = Guid.NewGuid().ToString(),
            });
            db.Submissions.Add(new MigrationSubmission
            {
                StoreRef = "nzbdav:release-1", State = "completed", UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var digest = new string('f', 64);
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 1, 0)));
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 1, 0, [Guid.NewGuid().ToString()])));
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 1, 0,
                [selected.LegacyDavItemId.ToString()])));
        await using var verify = harness.Mig();
        var batch = await verify.NzbDavBatches.SingleAsync();
        Assert.Equal("acknowledged", batch.Status);
        Assert.Equal(1, batch.AppliedCount);
        Assert.Equal(0, batch.ValidatedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcknowledgePlan_AllowsExactSourceThatBecameUnavailableAfterPlanning(bool replaced)
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("missing-source-ack", masterDigest, 0, 1);
        var selected = Assert.Single((await new NzbDavPackageReader().ReadAsync(packagePath))
            .Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "nzbdav:release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "nzbdav:release-1", MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "exact", NewDavItemId = Guid.NewGuid().ToString(),
            });
            db.Submissions.Add(new MigrationSubmission
            {
                StoreRef = "nzbdav:release-1", State = "completed", UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var digest = new string('f', 64);
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 0, 0)));
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 0, 0, null, [Guid.NewGuid().ToString()])));
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 0, 0, null,
                [selected.LegacyDavItemId.ToString()], [selected.LegacyDavItemId.ToString()])));
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 0, 0, null,
                replaced ? null : [selected.LegacyDavItemId.ToString()],
                replaced ? [selected.LegacyDavItemId.ToString()] : null)));
        await using var verify = harness.Mig();
        var batch = await verify.NzbDavBatches.SingleAsync();
        Assert.Equal("acknowledged", batch.Status);
        Assert.Equal(0, batch.AppliedCount);
        Assert.Equal(0, batch.ValidatedCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("digest")]
    [InlineData("reason")]
    [InlineData("submission")]
    [InlineData("provenance")]
    [InlineData("target")]
    public async Task ScanExclusion_PlanningAndAcknowledgementRequireConsistentRecordedEvidence(string conflict)
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("scan-excluded-ack", masterDigest, 0, 1);
        var package = await new NzbDavPackageReader().ReadAsync(packagePath);
        var selected = Assert.Single(package.Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        var runId = await harness.Store.BeginRunAsync();
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            const string storeRef = "nzbdav:release-1";
            var now = DateTime.UtcNow;
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = storeRef, StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", Verdict = "red", Included = false,
                VerdictReasons = "[\"inconsistent_source_category\"]", ScannedAt = now,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = storeRef, MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "scan-excluded", NewDavItemId = conflict == "target" ? Guid.NewGuid().ToString() : null,
                Flags = JsonSerializer.Serialize(new
                {
                    packageSha256 = conflict == "digest" ? new string('f', 64) : package.PackageDigest,
                    correlation = new
                    {
                        method = "scan-exclusion",
                        reasons = new[] { conflict == "reason" ? "category_unmapped" : "inconsistent_source_category" },
                    },
                }),
            });
            if (conflict == "submission")
                db.Submissions.Add(new MigrationSubmission { StoreRef = storeRef, State = "completed", UpdatedAt = now });
            if (conflict == "provenance")
                db.MigratedReleases.Add(new MigratedRelease
                {
                    SourceType = MigrationSourceTypes.NzbDav, SourceReleaseId = storeRef,
                    FirstRunId = runId, LastRunId = runId, MigratedAt = now, LastVerifiedAt = now,
                });
            await db.SaveChangesAsync();
        }
        var generated = await controller.GenerateCanaryPlan();
        var acknowledged = await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(new string('f', 64), 0, 0));
        if (conflict == "none")
        {
            Assert.IsType<OkObjectResult>(generated);
            Assert.IsType<FileContentResult>(await controller.DownloadCanaryPlan());
            Assert.IsType<OkObjectResult>(acknowledged);
            await using var verify = harness.Mig();
            Assert.Empty(await verify.CanaryLinks.ToListAsync());
            Assert.Equal("acknowledged", (await verify.NzbDavBatches.SingleAsync()).Status);
        }
        else
        {
            Assert.IsType<BadRequestObjectResult>(generated);
            Assert.IsType<BadRequestObjectResult>(acknowledged);
        }
    }

    [Fact]
    public async Task AcknowledgePlan_AllowsCompletedImportWithUnmatchedTargetToRemainUnlinked()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("unmatched-ack", masterDigest, 0, 1);
        var selected = Assert.Single((await new NzbDavPackageReader().ReadAsync(packagePath)).Manifest.SelectedLinks);
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 1, 1, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "nzbdav:release-1", StoreBasename = "release-1", SubmitFileName = "release.nzb",
                QueueFileName = "release.nzb", JobName = "release", VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow,
            });
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "nzbdav:release-1", MetaPath = "payload", VirtualPath = "/content/a.mkv",
                FileName = "a.mkv", NormalisedName = "a.mkv", SourceFileId = selected.LegacyDavItemId.ToString(),
                FileStatus = "unmatched-target",
            });
            db.Submissions.Add(new MigrationSubmission
            {
                StoreRef = "nzbdav:release-1", State = "completed", UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(new string('f', 64), 0, 0)));
        await using var verify = harness.Mig();
        Assert.Equal("acknowledged", (await verify.NzbDavBatches.SingleAsync()).Status);
    }

    [Fact]
    public async Task AcknowledgePlan_RequiresAllSuccessfulLinksButAllowsFailedReleaseToBeListed()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var masterDigest = new string('e', 64);
        var packagePath = await CreateFullPackageAsync("mixed-ack", masterDigest, 0, 1,
            includeSecondRelease: true);
        var selected = (await new NzbDavPackageReader().ReadAsync(packagePath)).Manifest.SelectedLinks;
        var controller = CreateController(harness);
        await controller.ConnectFull(new NzbDavFullConnectRequest(packagePath, masterDigest, 2, 2, 5, 1));
        await harness.Store.UpdateSessionAsync(session => session.Status = "complete");
        await using (var db = harness.Mig())
        {
            foreach (var (link, index) in selected.Select((link, index) => (link, index)))
            {
                var storeRef = $"nzbdav:release-{index + 1}";
                db.Releases.Add(new MigrationRelease
                {
                    StoreRef = storeRef, StoreBasename = $"release-{index + 1}",
                    SubmitFileName = $"release-{index + 1}.nzb",
                    QueueFileName = $"release-{index + 1}.nzb", JobName = $"release-{index + 1}",
                    VerdictReasons = "[]", ScannedAt = DateTime.UtcNow,
                });
                db.ReleaseFiles.Add(new MigrationReleaseFile
                {
                    StoreRef = storeRef, MetaPath = "payload", VirtualPath = $"/content/{index}.mkv",
                    FileName = $"{index}.mkv", NormalisedName = $"{index}.mkv",
                    SourceFileId = link.LegacyDavItemId.ToString(),
                    FileStatus = index == 0 ? "exact" : "import-failed",
                    NewDavItemId = index == 0 ? Guid.NewGuid().ToString() : null,
                });
                db.Submissions.Add(new MigrationSubmission
                {
                    StoreRef = storeRef, State = index == 0 ? "completed" : "failed",
                    Error = index == 0 ? null : "RAR signature not found", UpdatedAt = DateTime.UtcNow,
                });
            }
            await db.SaveChangesAsync();
        }
        var digest = new string('f', 64);
        Assert.IsType<BadRequestObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 0, 0)));
        Assert.IsType<OkObjectResult>(await controller.AcknowledgePlan(0,
            new NzbDavBatchPlanAcknowledgementRequest(digest, 1, 1)));
    }

    private NzbDavMigrationController CreateController(MigrationTestHarness harness)
    {
        var services = new ServiceCollection()
            .AddSingleton(new ConfigManager())
            .AddSingleton(harness.Store)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers["x-api-key"] = "nzbdav-controller-key";
        return new NzbDavMigrationController(harness.Store, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
    }

    private async Task<string> CreatePackageAsync(string name, int count, bool beneathInput = true)
    {
        var parent = beneathInput
            ? Path.Join(_config, "migration-input")
            : Path.Join(_config, "outside");
        Directory.CreateDirectory(parent);
        var payload = Path.Join(parent, $"{name}.nzb");
        await File.WriteAllTextAsync(payload, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />");
        var blob = Guid.NewGuid();
        var leaves = Enumerable.Range(0, count).Select(index => new NzbDavExportLeaf(
            Guid.NewGuid(), $"/content/Migration-TV/{index}.mkv", (index + 1) * 10, "release-1",
            null, blob, NzbDavArticleIdentity.DirectKind,
            Convert.ToHexString(SHA256.HashData([(byte)index])).ToLowerInvariant(), "ready", null)).ToArray();
        var links = leaves.Select((leaf, index) => new NzbDavSelectedLibraryLink(
            $"Migration-TV/{index}.mkv", $"/legacy/{index}", leaf.LegacyDavItemId)).ToArray();
        var output = Path.Join(parent, $"package-{name}-{Guid.NewGuid():N}");
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            name, output, [new CanaryExportRelease("release-1", blob, payload, leaves)], links));
        return output;
    }

    private async Task<string> CreateFullPackageAsync(
        string name,
        string masterDigest,
        int batchIndex,
        int batchCount,
        bool includeSecondRelease = false, string libraryPath = "Migration-TV/a.mkv", Guid? sourceId = null)
    {
        var parent = Path.Join(_config, "migration-input");
        Directory.CreateDirectory(parent);
        var payload = Path.Join(parent, $"{name}.nzb");
        await File.WriteAllTextAsync(payload, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />");
        var leafId = sourceId ?? Guid.NewGuid();
        var leaf = new NzbDavExportLeaf(leafId, "/content/a.mkv", 10, "release-1", null, null,
            NzbDavArticleIdentity.DirectKind, new string('a', 64), "ready", null);
        var output = Path.Join(parent, $"package-{name}-{Guid.NewGuid():N}");
        var releases = new List<CanaryExportRelease> { new("release-1", null, payload, [leaf]) };
        var links = new List<NzbDavSelectedLibraryLink>
            { new(libraryPath, "/legacy/a", leafId) };
        if (includeSecondRelease)
        {
            var secondPayload = Path.Join(parent, $"{name}-second.nzb");
            await File.WriteAllTextAsync(secondPayload, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />\n");
            var secondId = Guid.NewGuid();
            var secondLeaf = leaf with
            {
                LegacyDavItemId = secondId,
                LegacyPath = "/content/b.mkv",
                ParentReleaseId = "release-2",
            };
            releases.Add(new CanaryExportRelease("release-2", null, secondPayload, [secondLeaf]));
            links.Add(new NzbDavSelectedLibraryLink("Migration-TV/b.mkv", "/legacy/b", secondId));
        }
        await new CanaryPackageWriter().WriteFullBatchAsync(new CanaryExportRequest(
            name, output, releases, links),
            masterDigest, batchIndex, batchCount);
        return output;
    }

    public Task InitializeAsync()
    {
        _previousConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
        _previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("CONFIG_PATH", _config);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "nzbdav-controller-key");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfig);
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", _previousApiKey);
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
        return Task.CompletedTask;
    }
}
