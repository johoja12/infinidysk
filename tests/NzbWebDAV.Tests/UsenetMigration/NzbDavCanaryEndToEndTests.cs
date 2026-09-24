using Microsoft.EntityFrameworkCore;
using NzbDavMigration.Canary;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbDavMigration.Recovery;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Queue.FileAggregators;
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
    private const string DirectSourceReleaseId = "6f16ecdb-db31-418b-a7e4-2077eccaeeb3";
    private const string ArchiveSourceReleaseId = "ad7f4510-a108-4ddd-9880-e696cbd9dc72";
    private const string DirectSourceJobName = "Outlander.Blood.of.My.Blood.S02E01";
    private const string ArchiveSourceJobName = "Archive.Feature.2026";
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
            await using (var migration = harness.Mig())
            {
                var directRelease = await migration.Releases.SingleAsync(
                    release => release.StoreRef == $"nzbdav:{DirectSourceReleaseId}");
                Assert.Equal($"{DirectSourceJobName}.nzb", directRelease.SubmitFileName);
                Assert.Equal($"{DirectSourceJobName}.nzb", directRelease.QueueFileName);
                Assert.Equal(DirectSourceJobName, directRelease.JobName);
                Assert.Equal(
                    $"{DirectSourceJobName}.mkv",
                    ImportableVideoNamer.Normalize(
                        "b082fa0beaa644d3aa01045d5b8d0b36.xyz",
                        ".mkv",
                        directRelease.JobName,
                        allowBaseRename: true));
            }
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
            var sourceRoot = Path.Join(_root, "plex");
            var targetRoot = Path.Join(_root, "infinidysk");
            Directory.CreateDirectory(libraryRoot);
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(targetRoot);
            foreach (var link in planned.Plan.Links)
            {
                var source = Path.Join(
                    sourceRoot,
                    link.LibraryRelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.CreateSymbolicLink(source, link.OriginalLegacyTarget);
                var target = Path.Join(
                    targetRoot,
                    link.NewRelativeTarget!.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, new byte[checked((int)link.ExpectedFileSize)]);
            }

            var journalPath = Path.Join(_root, "apply-journal.json");
            var journal = await new CanaryLinkApplier(_ => true).ApplyAsync(
                Path.Join(planned.PlanDirectory, "plan.json"),
                sourceRoot,
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

    [Fact]
    public async Task FullRecoveryFixture_CataloguesExportsLinksAndUsesFinalCoverageSnapshot()
    {
        Directory.CreateDirectory(_root);
        var sourceRoot = Directory.CreateDirectory(Path.Join(_root, "source")).FullName;
        var libraryRoot = Directory.CreateDirectory(Path.Join(_root, "plex2")).FullName;
        var targetRoot = Directory.CreateDirectory(Path.Join(_root, "target")).FullName;
        var blobRoot = Path.Join(_root, "orphan-catalogue");
        CopyFixtureDirectory(FixtureDirectory(), blobRoot);

        var rows = BuildFullRecoveryRows();
        foreach (var (path, row) in rows)
            CreateLegacyLink(sourceRoot, path, row.Id);
        var inventoriedLinks = new LibraryInventoryService().Inventory(sourceRoot);
        Assert.Equal(rows.Count, inventoriedLinks.Count);
        var candidates = inventoriedLinks.Select(link => new LegacyInventoryCandidate(
            link.LibraryRelativePath, link.OriginalTarget, link.LegacyDavItemId,
            rows.Single(item => item.Row.Id == link.LegacyDavItemId).Row,
            link.LibraryRelativePath == "TV/Retained.mkv" ? "candidate" : "recoverable-orphan",
            null, null)).ToArray();
        var initialInventory = Path.Join(_root, "initial-inventory.json");
        await File.WriteAllTextAsync(initialInventory, System.Text.Json.JsonSerializer.Serialize(
            candidates, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));

        var frozenList = Path.Join(_root, "catalogue-input.json");
        var catalogue = Path.Join(_root, "catalogue.sqlite");
        var summary = Path.Join(_root, "catalogue-summary.json");
        Assert.Equal(0, await NzbDavMigrationProgram.RunAsync(
            ["catalogue-list", "--blob-root", blobRoot, "--output", frozenList]));
        Assert.Equal(0, await NzbDavMigrationProgram.RunAsync(
            ["catalogue-scan", "--blob-root", blobRoot, "--inventory", frozenList,
                "--database", catalogue, "--summary", summary]));
        await using (var store = new OrphanCatalogueStore(catalogue, summary))
        {
            await store.OpenCompletedAsync();
            Assert.Equal("invalid-xml", (await store.ReadBlobAsync("corrupt.nzb"))!.FailureClass);
            var original = await store.ReadBlobAsync("orphan-direct.nzb");
            var duplicate = await store.ReadBlobAsync("orphan-direct-byte-identical-copy.nzb");
            Assert.Equal(original!.Sha256, duplicate!.Sha256);
        }

        var recoveryRoot = Path.Join(_root, "recovery");
        await using (var store = new OrphanCatalogueStore(catalogue, catalogue + ".completion.json"))
        {
            await store.OpenCompletedAsync();
            var recovery = await new LegacySourceRecovery().WriteAsync(candidates, store, recoveryRoot, 0.90m);
            Assert.True(recovery.MeetsMinimumCoverage);
        }
        var masterPath = Path.Join(recoveryRoot, "master-manifest.json");
        var master = System.Text.Json.JsonSerializer.Deserialize<FullRecoveryMasterManifest>(
            await File.ReadAllTextAsync(masterPath),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(10, master.TotalLinks);
        Assert.Equal(9, master.RecoverableLinks);
        Assert.Equal(0.90m, master.RecoverableFraction);
        Assert.Equal(10, master.Items.Count);
        Assert.Equal("ambiguous-payload",
            master.Items.Single(item => item.LibraryRelativePath == "TV/Ambiguous.mkv").Classification);
        Assert.Equal("exact-archive",
            master.Items.Single(item => item.LibraryRelativePath == "TV/Renamed Lazy.mkv").Classification);

        var batchesRoot = Path.Join(_root, "batches");
        var releases = master.Items.Where(item => item.Classification is "exact-direct" or "exact-archive")
            .GroupBy(item => item.PayloadSha256, StringComparer.Ordinal)
            .Select(group =>
            {
                var relativePath = group.First().SourceRelativePath!;
                return new FullRecoveryRelease(group.Key!, group.Key!, relativePath,
                    new FileInfo(Path.Join(blobRoot, relativePath)).Length, group.ToArray());
            }).ToArray();
        var batches = new BatchPackagePlanner().Partition(releases, 2, 1_048_576);
        Directory.CreateDirectory(batchesRoot);
        var masterDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(masterPath))).ToLowerInvariant();
        foreach (var batch in batches)
        {
            var exportReleases = batch.Releases.Select(release =>
            {
                var names = NzbDavSourceNameResolver.FromLegacyPaths(release.Items.Select(item => item.LegacyPath!));
                var leaves = release.Items.Select(item => new NzbDavExportLeaf(
                    item.LegacyDavItemId, item.LegacyPath!, item.FileSize!.Value,
                    release.SourceReleaseId, null, null, item.IdentityKind!, item.IdentityDigest,
                    "ready", null,
                    item.Classification == "exact-archive" ? item.IdentityKind : null,
                    item.Classification == "exact-archive" ? item.IdentityDigest : null)).ToArray();
                return new CanaryExportRelease(release.SourceReleaseId, null,
                    Path.Join(blobRoot, release.PayloadRelativePath), leaves,
                    SourceFileName: names.FileName, SourceJobName: names.JobName);
            }).ToArray();
            var selected = batch.Releases.SelectMany(release => release.Items).Select(item =>
                new NzbDavSelectedLibraryLink(item.LibraryRelativePath, item.OriginalTarget,
                    item.LegacyDavItemId)).ToArray();
            var request = new CanaryExportRequest($"fixture-{batch.BatchIndex + 1}",
                Path.Join(batchesRoot, $"batch-{batch.BatchIndex + 1:D4}"), exportReleases, selected);
            await new CanaryPackageWriter().WriteFullBatchAsync(
                request, masterDigest, batch.BatchIndex, batches.Count);
        }
        var batchDirectories = Directory.GetDirectories(batchesRoot).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(2, batchDirectories.Length);
        foreach (var batch in batchDirectories)
        {
            var package = await new NzbDavPackageReader().ReadAsync(batch);
            Assert.Equal(NzbDavExportManifest.CurrentSchemaVersion, package.Manifest.SchemaVersion);
            Assert.Equal(2, package.Manifest.BatchCount);
            Assert.All(package.Manifest.SelectedLinks, selected => Assert.Contains(
                master.Items, item => item.LibraryRelativePath == selected.LibraryRelativePath &&
                                      item.Classification is "exact-direct" or "exact-archive"));
        }

        var sourceBeforeApply = SnapshotLinks(sourceRoot);
        var journalsRoot = Directory.CreateDirectory(Path.Join(_root, "journals")).FullName;
        var exact = master.Items.Where(item => item.Classification is "exact-direct" or "exact-archive").ToArray();
        foreach (var group in exact.Chunk(5).Select((items, index) => (items, index)))
        {
            var planLinks = group.items.Select(item =>
            {
                var relativeTarget = $".ids/full/{item.LegacyDavItemId}";
                var target = Path.Join(targetRoot, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, new byte[checked((int)item.FileSize!.Value)]);
                return new NzbDavCanaryPlanLink(item.LibraryRelativePath, item.OriginalTarget,
                    item.LegacyDavItemId, item.FileSize.Value, "exact", "{}", relativeTarget, "planned");
            }).ToArray();
            var plan = new NzbDavCanaryPlan(1, group.index + 1, new string('a', 64),
                DateTimeOffset.UtcNow, planLinks.Length, planLinks.Length, true, planLinks);
            var planDirectory = await new NzbDavCanaryPlanWriter().WriteAsync(
                Path.Join(_root, $"plans-{group.index + 1}"), plan);
            var journal = Path.Join(journalsRoot, $"batch-{group.index + 1}", "apply-journal.json");
            var applied = await new CanaryLinkApplier(_ => true).ApplyAsync(
                Path.Join(planDirectory, "plan.json"), sourceRoot, libraryRoot, targetRoot, journal);
            Assert.All(applied.Links, link => Assert.Equal("applied", link.Status));
            Assert.All(await new CanaryValidator().ValidateAsync(
                journal, maximumBytesPerRead: 8, timeout: TimeSpan.FromSeconds(2)),
                result => Assert.True(result.Success, result.Error));
        }
        Assert.Equal(sourceBeforeApply, SnapshotLinks(sourceRoot));

        File.Delete(Path.Join(sourceRoot, "TV", "Ambiguous.mkv"));
        CreateLegacyLink(sourceRoot, "TV/Added During Delta.mkv", Guid.NewGuid());
        var coverage = await new CanaryCoverageReporter().WriteAsync(
            sourceRoot, libraryRoot, initialInventory, masterPath, journalsRoot,
            Path.Join(_root, "coverage"), 0.90m, TimeSpan.FromSeconds(2));
        Assert.False(coverage.HasOwnershipErrors);
        Assert.True(coverage.Report.MeetsMinimumCoverage);
        Assert.Equal(10, coverage.Report.FinalSourceCount);
        Assert.Equal(9, coverage.Report.CoveredCount);
        Assert.Equal(1, coverage.Report.AddedCount);
        Assert.Equal(1, coverage.Report.RemovedCount);
        Assert.Equal(0.90m, coverage.Report.CoverageFraction);
        Assert.Equal(coverage.Report.FinalSourceCount, coverage.Report.Items.Count);
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
                    DirectSourceReleaseId,
                    directNzoId,
                    directPayload,
                    [new NzbDavExportLeaf(
                        directLeafId,
                        "/content/Migration-TV/direct-release/Direct.mkv",
                        220,
                        DirectSourceReleaseId,
                        null,
                        directNzoId,
                        NzbDavArticleIdentity.DirectKind,
                        NzbDavArticleIdentity.ComputeDirect(directSegments),
                        "ready",
                        null)],
                    SourceFileName: $"{DirectSourceJobName}.nzb",
                    SourceJobName: DirectSourceJobName),
                new CanaryExportRelease(
                    ArchiveSourceReleaseId,
                    archiveNzoId,
                    archivePayload,
                    [new NzbDavExportLeaf(
                        archiveLeafId,
                        "/content/Migration-TV/archive-release/Feature/Archive.mkv",
                        16,
                        ArchiveSourceReleaseId,
                        null,
                        archiveNzoId,
                        NzbDavStableArchiveIdentity.Kind,
                        NzbDavStableArchiveIdentity.Compute(
                            archiveReleaseDigest,
                            [new NzbDavArchivePartIdentity(archiveSegments, 0, 0, 0, 0)],
                            16),
                        "ready",
                        null)],
                    SourceFileName: $"{ArchiveSourceJobName}.nzb",
                    SourceJobName: ArchiveSourceJobName),
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
            [$"nzbdav:{DirectSourceReleaseId}"] = (fixture.DirectNzoId, fixture.DirectPayloadPath),
            [$"nzbdav:{ArchiveSourceReleaseId}"] = (fixture.ArchiveNzoId, fixture.ArchivePayloadPath),
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
        var directRelease = Folder(Guid.NewGuid(), category, DirectSourceJobName);
        var archiveRelease = Folder(Guid.NewGuid(), category, ArchiveSourceJobName);
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
            Completed(fixture.DirectNzoId, DirectSourceJobName),
            Completed(fixture.ArchiveNzoId, ArchiveSourceJobName));
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

    private static string FixtureDirectory() => Path.Join(
        AppContext.BaseDirectory, "Fixtures", "UsenetMigration", "orphan-catalogue");

    private static void CopyFixtureDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Join(destination, Path.GetFileName(file)));
    }

    private static List<(string Path, LegacyDavItemRow Row)> BuildFullRecoveryRows()
    {
        var rows = new List<(string Path, LegacyDavItemRow Row)>
        {
            Direct("TV/Retained.mkv", ["retained-direct-1@test", "retained-direct-2@test"], 16),
        };
        rows.AddRange(Enumerable.Range(1, 6).Select(index =>
            Direct($"TV/Orphan {index}.mkv", ["orphan-direct@test"], 16)));
        rows.Add(Archive("TV/Eager Archive.mkv", "eager-archive@test", 20));
        rows.Add(Archive("TV/Renamed Lazy.mkv", "renamed-lazy-archive@test", 24));
        rows.Add(Direct("TV/Ambiguous.mkv", ["ambiguous@test"], 12));
        return rows;
    }

    private static (string Path, LegacyDavItemRow Row) Direct(
        string libraryPath,
        string[] segmentIds,
        long size)
    {
        var id = Guid.NewGuid();
        return (libraryPath, new LegacyDavItemRow(
            id, $"/content/full-recovery/{libraryPath}", size, 3, null, null,
            NzbSegmentsJson: System.Text.Json.JsonSerializer.Serialize(segmentIds),
            HistoryExclusion: "missing-history"));
    }

    private static (string Path, LegacyDavItemRow Row) Archive(
        string libraryPath,
        string segmentId,
        long size)
    {
        var id = Guid.NewGuid();
        var parts = new[]
        {
            new DavRarFile.RarPart
            {
                SegmentIds = [segmentId],
                PartSize = size,
                Offset = 0,
                ByteCount = size,
            },
        };
        return (libraryPath, new LegacyDavItemRow(
            id, $"/content/full-recovery/{libraryPath}", size, 4, null, null,
            RarPartsJson: System.Text.Json.JsonSerializer.Serialize(parts),
            HistoryExclusion: "missing-history"));
    }

    private static void CreateLegacyLink(string root, string relativePath, Guid id)
    {
        var path = Path.Join(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, $"/mnt/legacy/.ids/a/b/c/d/e/{id}");
    }

    private static string[] SnapshotLinks(string root) => new LibraryInventoryService().Inventory(root)
        .Select(link => $"{link.LibraryRelativePath}\0{link.OriginalTarget}")
        .Order(StringComparer.Ordinal)
        .ToArray();

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
