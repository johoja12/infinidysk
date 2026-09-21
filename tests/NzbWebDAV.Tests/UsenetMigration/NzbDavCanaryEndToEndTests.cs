using Microsoft.EntityFrameworkCore;
using NzbDavMigration.Canary;
using NzbDavMigration.Export;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Canary;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

[Collection(nameof(ConfigPathCollection))]
public sealed class NzbDavCanaryEndToEndTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-e2e-{Guid.NewGuid():N}");

    [Fact]
    public async Task DirectAndArchivePackage_ReconcilesAppliesValidatesAndRollsBack()
    {
        Directory.CreateDirectory(_root);
        var previousBlobStore = BlobStore.Current;
        var blobStore = new MemoryBlobStore();
        BlobStore.Use(blobStore);
        try
        {
            await using var harness = await MigrationTestHarness.CreateAsync();
            var fixture = await CreatePackageAsync();
            await harness.Store.UpdateSessionAsync(session =>
            {
                session.Status = MigrationSessionStatus.Scanning;
                session.SourceType = MigrationSourceTypes.NzbDav;
                session.SourcePackageRoot = fixture.PackageRoot;
            });
            await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-canary", "migrate");

            var scan = await new NzbDavScanRunner(
                    harness.Store, new ConfigManager(), new NzbDavPackageReader())
                .ScanAsync();

            Assert.Equal(2, scan!.GreenCount);
            await StubSubmissionsAsync(harness, fixture);
            await SeedCompletedImportsAsync(harness, fixture, blobStore);

            var reconciler = new SubmissionReconciler(harness.Store)
            {
                DavContextFactory = harness.DavFactory,
            };
            var reconciled = await reconciler.ReconcileAsync();

            Assert.Equal(2, reconciled.Completed);
            long runId;
            await using (var migration = harness.Mig())
            {
                var session = await migration.SessionState.SingleAsync();
                runId = Assert.IsType<long>(session.CurrentRunId);
                var files = await migration.ReleaseFiles.OrderBy(file => file.VirtualPath).ToListAsync();
                Assert.Equal(2, files.Count);
                Assert.All(files, file => Assert.Equal("exact", file.FileStatus));
                Assert.Contains(files, file => file.ArticleIdentityKind == NzbDavArticleIdentity.DirectKind);
                Assert.Contains(files, file => file.ArticleIdentityKind == NzbDavStableArchiveIdentity.Kind);
                Assert.Equal(2, await migration.MigratedFiles.CountAsync());
                var run = await migration.MigrationRuns.SingleAsync(item => item.Id == runId);
                run.Status = "completed";
                run.CompletedAt = DateTime.UtcNow;
                await migration.SaveChangesAsync();
            }

            NzbDavCanaryPlanResult planned;
            await using (var migration = harness.Mig())
            {
                planned = await new NzbDavCanaryLinkPlanner().GenerateAsync(
                    migration, fixture.PackageRoot, runId, Path.Join(_root, "plans"));
            }
            Assert.True(planned.Plan.IsValid);
            Assert.Equal(2, planned.Plan.ActionableCount);
            Assert.All(planned.Plan.Links, link => Assert.Equal("exact", link.CorrelationStatus));

            var libraryRoot = Path.Join(_root, "plex2");
            var targetRoot = Path.Join(_root, "infinidysk");
            Directory.CreateDirectory(libraryRoot);
            Directory.CreateDirectory(targetRoot);
            foreach (var link in planned.Plan.Links)
            {
                var target = Path.Join(
                    targetRoot,
                    link.NewRelativeTarget!.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, new byte[checked((int)link.ExpectedFileSize)]);
            }

            var journalPath = Path.Join(_root, "apply-journal.json");
            var journal = await new CanaryLinkApplier(_ => true).ApplyAsync(
                Path.Join(planned.PlanDirectory, "plan.json"),
                libraryRoot,
                targetRoot,
                journalPath);

            Assert.Equal(2, journal.Links.Count);
            Assert.All(journal.Links, link => Assert.Equal("applied", link.Status));
            var validation = await new CanaryValidator().ValidateAsync(
                journalPath, maximumBytesPerRead: 8, timeout: TimeSpan.FromSeconds(2));
            Assert.Equal(2, validation.Count);
            Assert.All(validation, result =>
            {
                Assert.True(result.Success, result.Error);
                Assert.Equal(["beginning", "middle", "end"], result.Reads.Select(read => read.Position));
            });

            var rollback = await new CanaryLinkRollback().RollbackAsync(journalPath);

            Assert.Equal(2, rollback.RemovedLinks);
            Assert.Equal(0, rollback.SkippedLinks);
            Assert.All(journal.Links, link => Assert.False(File.Exists(link.LinkPath)));
        }
        finally
        {
            BlobStore.Use(previousBlobStore);
        }
    }

    private async Task<CanaryFixture> CreatePackageAsync()
    {
        var directSegments = new[]
        {
            new NzbDavArticleSegment(1, 100, "direct-1@test"),
            new NzbDavArticleSegment(2, 120, "direct-2@test"),
        };
        var archiveSegments = new[]
        {
            new NzbDavArticleSegment(1, 10, "archive-1@test"),
            new NzbDavArticleSegment(1, 12, "archive-2@test"),
        };
        var directPayload = Path.Join(_root, "direct.nzb");
        var archivePayload = Path.Join(_root, "archive.nzb");
        await File.WriteAllTextAsync(directPayload, Nzb(
            ("direct.mkv", directSegments)));
        await File.WriteAllTextAsync(archivePayload, Nzb(
            ("archive.part1.rar", [archiveSegments[0]]),
            ("archive.part2.rar", [archiveSegments[1]])));

        var directLeafId = Guid.NewGuid();
        var archiveLeafId = Guid.NewGuid();
        var directNzoId = Guid.NewGuid();
        var archiveNzoId = Guid.NewGuid();
        var archiveReleaseDigest = NzbDavArticleIdentity.ComputeRelease(
            archiveSegments.Select(segment => new[] { segment }));
        var package = Path.Join(_root, "package");
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "e2e-canary",
            package,
            [
                new CanaryExportRelease(
                    "direct-release",
                    directNzoId,
                    directPayload,
                    [new NzbDavExportLeaf(
                        directLeafId,
                        "/content/Migration-TV/direct-release/Direct.mkv",
                        220,
                        "direct-release",
                        null,
                        directNzoId,
                        NzbDavArticleIdentity.DirectKind,
                        NzbDavArticleIdentity.ComputeDirect(directSegments),
                        "ready",
                        null)]),
                new CanaryExportRelease(
                    "archive-release",
                    archiveNzoId,
                    archivePayload,
                    [new NzbDavExportLeaf(
                        archiveLeafId,
                        "/content/Migration-TV/archive-release/Feature/Archive.mkv",
                        16,
                        "archive-release",
                        null,
                        archiveNzoId,
                        NzbDavStableArchiveIdentity.Kind,
                        NzbDavStableArchiveIdentity.Compute(
                            archiveReleaseDigest,
                            [new NzbDavArchivePartIdentity(archiveSegments, 0, 0, 0, 0)],
                            16),
                        "ready",
                        null)]),
            ],
            [
                new NzbDavSelectedLibraryLink(
                    "Migration-TV/Direct.mkv", "/legacy/.ids/direct", directLeafId),
                new NzbDavSelectedLibraryLink(
                    "Migration-TV/Archive.mkv", "/legacy/.ids/archive", archiveLeafId),
            ]));
        return new CanaryFixture(
            package,
            directPayload,
            archivePayload,
            directNzoId,
            archiveNzoId,
            directSegments.Select(segment => segment.MessageId).ToArray(),
            archiveSegments.Select(segment => segment.MessageId).ToArray());
    }

    private static async Task StubSubmissionsAsync(MigrationTestHarness harness, CanaryFixture fixture)
    {
        var expected = new Dictionary<string, (Guid NzoId, string PayloadPath)>(StringComparer.Ordinal)
        {
            ["nzbdav:direct-release"] = (fixture.DirectNzoId, fixture.DirectPayloadPath),
            ["nzbdav:archive-release"] = (fixture.ArchiveNzoId, fixture.ArchivePayloadPath),
        };
        var session = await harness.Store.GetSessionAsync();
        await using var migration = harness.Mig();
        var submissions = await migration.Submissions.OrderBy(item => item.StoreRef).ToListAsync();
        foreach (var submission in submissions)
        {
            var release = await migration.Releases.SingleAsync(item => item.StoreRef == submission.StoreRef);
            var payload = await new NzbDavPayloadBuilder(new NzbDavPackageReader())
                .BuildAsync(release, session, migration);
            var stub = expected[submission.StoreRef];
            Assert.Equal(await File.ReadAllBytesAsync(stub.PayloadPath), payload);
            submission.NzoId = stub.NzoId.ToString();
            submission.State = "submitted";
            submission.UpdatedAt = DateTime.UtcNow;
        }
        await migration.SaveChangesAsync();
    }

    private static async Task SeedCompletedImportsAsync(
        MigrationTestHarness harness,
        CanaryFixture fixture,
        IBlobStore blobStore)
    {
        await using (var direct = File.OpenRead(fixture.DirectPayloadPath))
            await blobStore.WriteBlob(fixture.DirectNzoId, direct);
        await using (var archive = File.OpenRead(fixture.ArchivePayloadPath))
            await blobStore.WriteBlob(fixture.ArchiveNzoId, archive);

        var directTargetId = Guid.NewGuid();
        var archiveTargetId = Guid.NewGuid();
        var directFileBlobId = Guid.NewGuid();
        var archiveFileBlobId = Guid.NewGuid();
        await blobStore.WriteBlob(directFileBlobId, new DavNzbFile
        {
            Id = directFileBlobId,
            SegmentIds = fixture.DirectSegmentIds,
        });
        await blobStore.WriteBlob(archiveFileBlobId, new DavRarFile
        {
            Id = archiveFileBlobId,
            RarParts =
            [
                new DavRarFile.RarPart { SegmentIds = fixture.ArchiveSegmentIds },
            ],
        });

        await using var dav = harness.Dav();
        var category = Folder(Guid.NewGuid(), DavItem.ContentFolder, "migration-canary");
        var directRelease = Folder(Guid.NewGuid(), category, "direct-release");
        var archiveRelease = Folder(Guid.NewGuid(), category, "archive-release");
        var archiveFeature = Folder(Guid.NewGuid(), archiveRelease, "Feature");
        dav.Items.AddRange(category, directRelease, archiveRelease, archiveFeature);
        dav.Items.Add(DavItem.New(
            directTargetId,
            directRelease,
            "Direct.mkv",
            220,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            null,
            null,
            fixture.DirectNzoId,
            directFileBlobId,
            fixture.DirectNzoId));
        dav.Items.Add(DavItem.New(
            archiveTargetId,
            archiveFeature,
            "Archive.mkv",
            16,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.RarFile,
            null,
            null,
            fixture.ArchiveNzoId,
            archiveFileBlobId,
            fixture.ArchiveNzoId));
        dav.HistoryItems.AddRange(
            Completed(fixture.DirectNzoId, "direct-release"),
            Completed(fixture.ArchiveNzoId, "archive-release"));
        await dav.SaveChangesAsync();
    }

    private static DavItem Folder(Guid id, DavItem parent, string name) => DavItem.New(
        id,
        parent,
        name,
        null,
        DavItem.ItemType.Directory,
        DavItem.ItemSubType.Directory,
        null,
        null,
        null,
        null);

    private static HistoryItem Completed(Guid id, string jobName) => new()
    {
        Id = id,
        CreatedAt = DateTime.UtcNow,
        FileName = $"{jobName}.nzb",
        JobName = jobName,
        Category = "migration-canary",
        DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
    };

    private static string Nzb(params (string Subject, NzbDavArticleSegment[] Segments)[] files) =>
        "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">" + string.Concat(files.Select(file =>
            $"<file subject=\"{file.Subject}\"><groups><group>alt.test</group></groups><segments>" +
            string.Concat(file.Segments.Select(segment =>
                $"<segment bytes=\"{segment.Bytes}\" number=\"{segment.Number}\">{segment.MessageId}</segment>")) +
            "</segments></file>")) + "</nzb>";

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record CanaryFixture(
        string PackageRoot,
        string DirectPayloadPath,
        string ArchivePayloadPath,
        Guid DirectNzoId,
        Guid ArchiveNzoId,
        string[] DirectSegmentIds,
        string[] ArchiveSegmentIds);

    private sealed class MemoryBlobStore : IBlobStore
    {
        private readonly Dictionary<Guid, byte[]> _raw = [];
        private readonly Dictionary<Guid, object> _metadata = [];

        public async Task WriteBlob(
            Guid id,
            Stream stream,
            CancellationToken cancellationToken = default)
        {
            await using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken);
            _raw[id] = copy.ToArray();
        }

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _metadata[id] = blob!;
            return Task.CompletedTask;
        }

        public Stream? ReadBlob(Guid id) =>
            _raw.TryGetValue(id, out var bytes) ? new MemoryStream(bytes, writable: false) : null;

        public Task<T?> ReadBlob<T>(Guid id) =>
            Task.FromResult(_metadata.TryGetValue(id, out var value) ? (T?)value : default);

        public bool Exists(Guid id) => _raw.ContainsKey(id) || _metadata.ContainsKey(id);

        public bool Delete(Guid id) => _raw.Remove(id) | _metadata.Remove(id);
    }
}
