using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbDavMigration.Export;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Models;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Naming;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Provenance;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavReconciliationServiceTests : IDisposable
{
    private readonly string _root = Path.Join(
        Path.GetTempPath(), $"nzbdav-reconciliation-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReconcileAsync_IsIdempotentAndNeverWritesQueueHistoryOrDavItems()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var fixture = await SeedAsync(harness);
        int queueCount;
        int historyCount;
        int itemCount;
        await using (var before = harness.Dav())
        {
            queueCount = await before.QueueItems.CountAsync();
            historyCount = await before.HistoryItems.CountAsync();
            itemCount = await before.Items.CountAsync();
        }
        var service = new NzbDavReconciliationService(
            harness.Store, new NzbDavPackageReader(), fixture.BlobStore)
        {
            DavContextFactory = harness.DavFactory,
        };

        var first = await service.ReconcileAsync(fixture.RunId, fixture.PackageRoot);
        await using var firstLedger = harness.Mig();
        var firstTargets = await firstLedger.ReleaseFiles.OrderBy(file => file.Id)
            .Select(file => file.NewDavItemId).ToArrayAsync();
        await firstLedger.DisposeAsync();
        var second = await service.ReconcileAsync(fixture.RunId, fixture.PackageRoot);
        await using var secondLedger = harness.Mig();
        var secondTargets = await secondLedger.ReleaseFiles.OrderBy(file => file.Id)
            .Select(file => file.NewDavItemId).ToArrayAsync();

        Assert.Equal(4, first.SelectedCount);
        Assert.Equal(3, first.ExactCount);
        Assert.Equal(1, first.AmbiguousCount);
        Assert.Equal(0, first.SubmittedCount);
        Assert.Equal(first, second);
        Assert.Equal(firstTargets, secondTargets);
        await using var after = harness.Dav();
        Assert.Equal(queueCount, await after.QueueItems.CountAsync());
        Assert.Equal(historyCount, await after.HistoryItems.CountAsync());
        Assert.Equal(itemCount, await after.Items.CountAsync());
    }

    [Fact]
    public async Task ReconcileAsync_RefusesToReplaceExistingExactTarget()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var fixture = await SeedAsync(harness);
        var conflictingTarget = Guid.NewGuid();
        await using (var db = harness.Mig())
        {
            var direct = await db.ReleaseFiles.SingleAsync(file => file.StoreRef == "nzbdav:direct");
            direct.FileStatus = "exact";
            direct.NewDavItemId = conflictingTarget.ToString();
            var migrated = await db.MigratedFiles.SingleAsync(file => file.SourceFileId == direct.SourceFileId);
            migrated.DavItemId = conflictingTarget;
            await db.SaveChangesAsync();
        }
        var service = new NzbDavReconciliationService(
            harness.Store, new NzbDavPackageReader(), fixture.BlobStore)
        {
            DavContextFactory = harness.DavFactory,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync(fixture.RunId, fixture.PackageRoot));

        await using var verify = harness.Mig();
        var source = await verify.ReleaseFiles.SingleAsync(file => file.StoreRef == "nzbdav:direct");
        var persisted = await verify.MigratedFiles.SingleAsync(file => file.SourceFileId == source.SourceFileId);
        Assert.Equal(conflictingTarget.ToString(), source.NewDavItemId);
        Assert.Equal(conflictingTarget, persisted.DavItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_RejectsMissingOrMismatchedPackageDigest(bool mismatched)
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var fixture = await SeedAsync(harness);
        int queueCount;
        int historyCount;
        int itemCount;
        await using (var before = harness.Dav())
        {
            queueCount = await before.QueueItems.CountAsync();
            historyCount = await before.HistoryItems.CountAsync();
            itemCount = await before.Items.CountAsync();
        }
        string?[] targetsBefore;
        await using (var db = harness.Mig())
        {
            var files = await db.ReleaseFiles.OrderBy(file => file.Id).ToListAsync();
            targetsBefore = files.Select(file => file.NewDavItemId).ToArray();
            foreach (var file in files)
            {
                file.Flags = mismatched
                    ? JsonSerializer.Serialize(new { packageSha256 = new string('f', 64) })
                    : """{"correlation":{"match":"article-identity"}}""";
            }
            await db.SaveChangesAsync();
        }
        var service = new NzbDavReconciliationService(
            harness.Store, new NzbDavPackageReader(), fixture.BlobStore)
        {
            DavContextFactory = harness.DavFactory,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ReconcileAsync(fixture.RunId, fixture.PackageRoot));

        await using var verifyLedger = harness.Mig();
        Assert.Equal(targetsBefore, await verifyLedger.ReleaseFiles.OrderBy(file => file.Id)
            .Select(file => file.NewDavItemId).ToArrayAsync());
        await using var verifyDav = harness.Dav();
        Assert.Equal(queueCount, await verifyDav.QueueItems.CountAsync());
        Assert.Equal(historyCount, await verifyDav.HistoryItems.CountAsync());
        Assert.Equal(itemCount, await verifyDav.Items.CountAsync());
    }

    private async Task<Fixture> SeedAsync(MigrationTestHarness harness)
    {
        Directory.CreateDirectory(_root);
        var definitions = new[]
        {
            new Definition("direct", 100, "direct@test", NzbDavArticleIdentity.DirectKind, "direct.mkv"),
            new Definition("path", 80, "path@test", NzbDavArticleIdentity.ArchiveMemberKind, "legacy/path.mkv"),
            new Definition("unique", 90, "unique@test", NzbDavArticleIdentity.ArchiveMemberKind, "legacy/old.mkv"),
            new Definition("ambiguous", 70, "ambiguous@test", NzbDavArticleIdentity.ArchiveMemberKind, "legacy/ambiguous.mkv"),
        };
        var exports = new List<CanaryExportRelease>();
        var links = new List<NzbDavSelectedLibraryLink>();
        foreach (var definition in definitions)
        {
            var sourceNzbId = Guid.NewGuid();
            var legacyId = Guid.NewGuid();
            var payload = Path.Join(_root, $"{definition.Name}.nzb");
            await File.WriteAllTextAsync(payload, Nzb(definition.MessageId, definition.Size + 20));
            var releaseDigest = NzbDavArticleIdentity.ComputeRelease(
                [[new NzbDavArticleSegment(1, definition.Size + 20, definition.MessageId)]]);
            var digest = definition.Kind == NzbDavArticleIdentity.DirectKind
                ? NzbDavArticleIdentity.ComputeDirect(
                    [new NzbDavArticleSegment(1, definition.Size + 20, definition.MessageId)])
                : NzbDavArticleIdentity.ComputeArchiveMember(
                    releaseDigest, definition.LegacyInnerPath, definition.Size);
            exports.Add(new CanaryExportRelease(
                definition.Name,
                sourceNzbId,
                payload,
                [new NzbDavExportLeaf(
                    legacyId,
                    $"/content/tv/{definition.Name}/{Path.GetFileName(definition.LegacyInnerPath)}",
                    definition.Size,
                    definition.Name,
                    null,
                    sourceNzbId,
                    definition.Kind,
                    digest,
                    "ready",
                    null)]));
            links.Add(new NzbDavSelectedLibraryLink(
                $"TV/{definition.Name}.mkv", $"/legacy/.ids/{legacyId}", legacyId));
        }
        var packageRoot = Path.Join(_root, "package");
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "phase-b-reconcile", packageRoot, exports, links));
        var package = await new NzbDavPackageReader().ReadAsync(packageRoot);
        var blobStore = new MemoryBlobStore();
        var now = DateTime.UtcNow;
        long runId;
        await using (var db = harness.Mig())
        {
            var run = new MigrationRun
            {
                SourceType = MigrationSourceTypes.NzbDav,
                Status = "completed",
                StartedAt = now,
                CompletedAt = now,
            };
            db.MigrationRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
            var session = await UsenetMigrationStore.GetOrCreateSessionAsync(db);
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = packageRoot;
            session.Status = "complete";
            session.CurrentRunId = runId;
            session.RunCompletedAt = now;
            foreach (var release in package.Manifest.Releases)
            {
                var storeRef = $"nzbdav:{release.SourceReleaseId}";
                var nzoId = Guid.NewGuid();
                db.Releases.Add(new MigrationRelease
                {
                    StoreRef = storeRef,
                    StoreBasename = release.SourceReleaseId,
                    SubmitFileName = $"{release.SourceReleaseId}.nzb",
                    QueueFileName = $"{release.SourceReleaseId}.nzb",
                    JobName = release.SourceReleaseId,
                    VerdictReasons = "[]",
                    ScannedAt = now,
                });
                var leaf = Assert.Single(release.Leaves);
                var source = new MigrationReleaseFile
                {
                    StoreRef = storeRef,
                    MetaPath = release.PayloadPath,
                    VirtualPath = leaf.LegacyPath,
                    FileName = Path.GetFileName(leaf.LegacyPath),
                    NormalisedName = MatchKey.ForLeaf(Path.GetFileName(leaf.LegacyPath)),
                    FileSize = leaf.FileSize,
                    FileStatus = "unmatched-target",
                    SourceFileId = leaf.LegacyDavItemId.ToString(),
                    ArticleIdentityKind = leaf.IdentityKind,
                    ArticleIdentityDigest = leaf.IdentityDigest,
                    Flags = JsonSerializer.Serialize(new { packageSha256 = package.PackageDigest }),
                };
                db.ReleaseFiles.Add(source);
                db.Submissions.Add(new MigrationSubmission
                {
                    StoreRef = storeRef,
                    NzoId = nzoId.ToString(),
                    State = "completed",
                    CompletedAt = now,
                    UpdatedAt = now,
                });
                var migrated = new MigratedRelease
                {
                    SourceType = MigrationSourceTypes.NzbDav,
                    SourceReleaseId = storeRef,
                    FirstRunId = runId,
                    LastRunId = runId,
                    NzoId = nzoId.ToString(),
                    ExpectedFileCount = 1,
                    MappedFileCount = 0,
                    MigratedAt = now,
                    LastVerifiedAt = now,
                };
                db.MigratedReleases.Add(migrated);
                await db.SaveChangesAsync();
                if (release.SourceReleaseId == "direct")
                {
                    var target = await SeedTargetAsync(
                        harness, blobStore, release.SourceReleaseId, nzoId, leaf.FileSize,
                        leaf.LegacyPath, release.SourceReleaseId, duplicate: false);
                    source.FileStatus = "exact";
                    source.NewDavItemId = target.ToString();
                    migrated.MappedFileCount = 1;
                    db.MigratedFiles.Add(Migrated(source, migrated.Id, target, nzoId, now));
                }
                else
                {
                    await SeedTargetAsync(
                        harness, blobStore, release.SourceReleaseId, nzoId, leaf.FileSize,
                        leaf.LegacyPath, release.SourceReleaseId,
                        duplicate: release.SourceReleaseId == "ambiguous");
                }
            }
            await db.SaveChangesAsync();
        }
        return new Fixture(packageRoot, runId, blobStore);
    }

    private static async Task<Guid> SeedTargetAsync(
        MigrationTestHarness harness,
        MemoryBlobStore blobStore,
        string release,
        Guid nzoId,
        long size,
        string legacyPath,
        string messagePrefix,
        bool duplicate)
    {
        var messageId = $"{messagePrefix}@test";
        await ((IBlobStore)blobStore).WriteBlob(
            nzoId,
            new MemoryStream(Encoding.UTF8.GetBytes(Nzb(messageId, size + 20))));
        var count = duplicate ? 2 : 1;
        Guid first = Guid.Empty;
        await using var dav = harness.Dav();
        dav.HistoryItems.Add(new HistoryItem
        {
            Id = nzoId,
            CreatedAt = DateTime.UtcNow,
            FileName = $"{release}.nzb",
            JobName = release,
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        });
        for (var index = 0; index < count; index++)
        {
            var targetId = Guid.NewGuid();
            if (index == 0) first = targetId;
            var fileBlobId = Guid.NewGuid();
            DavItem.ItemSubType subtype;
            if (release == "direct")
            {
                subtype = DavItem.ItemSubType.NzbFile;
                await blobStore.WriteBlob(fileBlobId, new DavNzbFile
                {
                    Id = fileBlobId,
                    SegmentIds = [messageId],
                });
            }
            else if (release == "path")
            {
                subtype = DavItem.ItemSubType.MultipartFile;
                await blobStore.WriteBlob(fileBlobId, new DavMultipartFile
                {
                    Id = fileBlobId,
                    Metadata = new DavMultipartFile.Meta
                    {
                        PathInArchive = "legacy/path.mkv",
                        FileParts =
                        [
                            new DavMultipartFile.FilePart
                            {
                                SegmentIds = [messageId],
                                SegmentIdByteRange = LongRange.FromStartAndSize(0, size + 20),
                                FilePartByteRange = LongRange.FromStartAndSize(10, size),
                            },
                        ],
                    },
                });
            }
            else
            {
                subtype = DavItem.ItemSubType.RarFile;
                await blobStore.WriteBlob(fileBlobId, new DavRarFile
                {
                    Id = fileBlobId,
                    RarParts =
                    [
                        new DavRarFile.RarPart
                        {
                            SegmentIds = [messageId],
                            PartSize = size + 20,
                            Offset = 10,
                            ByteCount = size,
                        },
                    ],
                });
            }
            dav.Items.Add(DavItem.New(
                targetId,
                DavItem.ContentFolder,
                duplicate ? $"{release}-{index}.mkv" : $"renamed-{release}.mkv",
                size,
                DavItem.ItemType.UsenetFile,
                subtype,
                null,
                null,
                nzoId,
                fileBlobId,
                nzoId));
        }
        await dav.SaveChangesAsync();
        return first;
    }

    private static MigratedFile Migrated(
        MigrationReleaseFile source,
        long migratedReleaseId,
        Guid target,
        Guid nzoId,
        DateTime now) => new()
    {
        MigratedReleaseId = migratedReleaseId,
        VirtualPath = source.VirtualPath,
        NormalisedRelativePath = MatchKey.ForRelativePath(source.VirtualPath),
        NormalisedName = source.NormalisedName,
        FileSize = source.FileSize,
        DavItemId = target,
        NzbBlobId = nzoId,
        SourceFileId = source.SourceFileId,
        ArticleIdentityKind = source.ArticleIdentityKind,
        ArticleIdentityDigest = source.ArticleIdentityDigest,
        MatchMethod = "article-identity",
        LastVerifiedAt = now,
    };

    private static string Nzb(string messageId, long bytes) =>
        "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">"
        + $"<file subject=\"release\"><groups><group>alt.test</group></groups><segments>"
        + $"<segment bytes=\"{bytes}\" number=\"1\">{messageId}</segment>"
        + "</segments></file></nzb>";

    private sealed record Definition(
        string Name,
        long Size,
        string MessageId,
        string Kind,
        string LegacyInnerPath);

    private sealed record Fixture(string PackageRoot, long RunId, MemoryBlobStore BlobStore);

    private sealed class MemoryBlobStore : IBlobStore
    {
        private readonly Dictionary<Guid, byte[]> _raw = [];
        private readonly Dictionary<Guid, object> _metadata = [];

        public async Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default)
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
