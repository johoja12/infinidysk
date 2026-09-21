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
