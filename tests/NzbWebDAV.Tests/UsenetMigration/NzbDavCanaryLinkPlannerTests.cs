using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbDavMigration.Export;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Canary;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavCanaryLinkPlannerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-canary-plan-{Guid.NewGuid():N}");

    [Fact]
    public async Task GenerateAsync_RejectsAnyNonExactSelectionWithoutPersistingRowsOrPlan()
    {
        var exactSource = Guid.NewGuid();
        var unmatchedSource = Guid.NewGuid();
        var exactTarget = Guid.NewGuid();
        var package = await CreatePackageAsync(
            new NzbDavSelectedLibraryLink("TV/Show/exact.mkv", "/legacy/.ids/exact", exactSource),
            new NzbDavSelectedLibraryLink("TV/Show/unmatched.mkv", "/legacy/.ids/unmatched", unmatchedSource));
        await using var harness = await MigrationTestHarness.CreateAsync();
        await SeedAsync(harness, exactSource, unmatchedSource, exactTarget);

        await using var db = harness.Mig();
        var output = Path.Join(_root, "output");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NzbDavCanaryLinkPlanner().GenerateAsync(db, package, 42, output));

        Assert.Contains("every selected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.CanaryLinks.ToListAsync());
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task GenerateAsync_RejectsDuplicateLibraryOutputPathsWithoutPersistingRows()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var package = await CreatePackageAsync(
            new NzbDavSelectedLibraryLink("TV/duplicate.mkv", "/legacy/one", first),
            new NzbDavSelectedLibraryLink("TV/other.mkv", "/legacy/two", second));
        var manifestPath = Path.Join(package, "manifest.json");
        var manifest = NzbDavExportManifestJson.Deserialize(await File.ReadAllTextAsync(manifestPath));
        await File.WriteAllTextAsync(manifestPath, NzbDavExportManifestJson.Serialize(manifest with
        {
            SelectedLinks =
            [
                manifest.SelectedLinks[0],
                manifest.SelectedLinks[1] with { LibraryRelativePath = "TV/duplicate.mkv" },
            ],
        }));
        await RefreshManifestChecksumAsync(package);
        await using var harness = await MigrationTestHarness.CreateAsync();

        await using var db = harness.Mig();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NzbDavCanaryLinkPlanner().GenerateAsync(db, package, 42, Path.Join(_root, "output")));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.CanaryLinks.ToListAsync());
    }

    [Fact]
    public async Task GenerateAsync_RefusesToOverwriteAnImmutablePlan()
    {
        var source = Guid.NewGuid();
        var package = await CreatePackageAsync(
            new NzbDavSelectedLibraryLink("TV/one.mkv", "/legacy/one", source));
        await using var harness = await MigrationTestHarness.CreateAsync();
        await SeedAsync(harness, source, null, Guid.NewGuid());
        var output = Path.Join(_root, "output");

        await using (var db = harness.Mig())
            await new NzbDavCanaryLinkPlanner().GenerateAsync(db, package, 42, output);
        await using var retry = harness.Mig();

        await Assert.ThrowsAsync<IOException>(() =>
            new NzbDavCanaryLinkPlanner().GenerateAsync(retry, package, 42, output));
    }

    private async Task<string> CreatePackageAsync(params NzbDavSelectedLibraryLink[] links)
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Join(_root, $"{Guid.NewGuid():N}.nzb");
        await File.WriteAllTextAsync(payload, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />");
        var blobId = Guid.NewGuid();
        var leaves = links.Select((link, index) => new NzbDavExportLeaf(
            link.LegacyDavItemId,
            $"/content/{link.LibraryRelativePath}",
            (index + 1) * 10,
            "release-1",
            null,
            blobId,
            NzbDavArticleIdentity.DirectKind,
            new string((char)('a' + index), 64),
            "ready",
            null)).ToArray();
        var package = Path.Join(_root, $"package-{Guid.NewGuid():N}");
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "test-package",
            package,
            [new CanaryExportRelease("release-1", blobId, payload, leaves)],
            links));
        return package;
    }

    private static async Task SeedAsync(
        MigrationTestHarness harness,
        Guid exactSource,
        Guid? unmatchedSource,
        Guid exactTarget)
    {
        await using var db = harness.Mig();
        var now = DateTime.UtcNow;
        db.MigrationRuns.Add(new MigrationRun
        {
            Id = 42,
            SourceType = MigrationSourceTypes.NzbDav,
            Status = "completed",
            StartedAt = now,
            CompletedAt = now,
        });
        db.Releases.Add(new MigrationRelease
        {
            StoreRef = "release-1",
            StoreBasename = "release-1",
            SubmitFileName = "release-1.nzb",
            QueueFileName = "release-1.nzb",
            JobName = "release-1",
            VerdictReasons = "[]",
            ScannedAt = now,
        });
        var exactFile = new MigrationReleaseFile
        {
            StoreRef = "release-1",
            MetaPath = "package:release-1",
            VirtualPath = "/content/TV/Show/exact.mkv",
            FileName = "exact.mkv",
            NormalisedName = "exact.mkv",
            FileSize = 10,
            FileStatus = "exact",
            SourceFileId = exactSource.ToString(),
            Flags = "{\"match\":\"strong\"}",
            NewDavItemId = exactTarget.ToString(),
        };
        db.ReleaseFiles.Add(exactFile);
        if (unmatchedSource is { } missing)
        {
            db.ReleaseFiles.Add(new MigrationReleaseFile
            {
                StoreRef = "release-1",
                MetaPath = "package:release-1",
                VirtualPath = "/content/TV/Show/unmatched.mkv",
                FileName = "unmatched.mkv",
                NormalisedName = "unmatched.mkv",
                FileSize = 20,
                FileStatus = "unmatched-target",
                SourceFileId = missing.ToString(),
                Flags = "{\"candidates\":[]}",
            });
        }
        var migrated = new MigratedRelease
        {
            SourceType = MigrationSourceTypes.NzbDav,
            SourceReleaseId = "release-1",
            FirstRunId = 42,
            LastRunId = 42,
            ExpectedFileCount = unmatchedSource is null ? 1 : 2,
            MappedFileCount = 1,
            MigratedAt = now,
            LastVerifiedAt = now,
        };
        db.MigratedReleases.Add(migrated);
        await db.SaveChangesAsync();
        db.MigratedFiles.Add(new MigratedFile
        {
            MigratedReleaseId = migrated.Id,
            VirtualPath = exactFile.VirtualPath,
            NormalisedRelativePath = "tv/show/exact.mkv",
            NormalisedName = "exact.mkv",
            FileSize = 10,
            DavItemId = exactTarget,
            SourceFileId = exactSource.ToString(),
            MatchMethod = "article-identity",
            LastVerifiedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task RefreshManifestChecksumAsync(string package)
    {
        var manifestPath = Path.Join(package, "manifest.json");
        var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)))
            .ToLowerInvariant();
        var sumsPath = Path.Join(package, "SHA256SUMS");
        var lines = (await File.ReadAllLinesAsync(sumsPath)).Select(line =>
            line.EndsWith("  manifest.json", StringComparison.Ordinal)
                ? $"{digest}  manifest.json"
                : line);
        await File.WriteAllTextAsync(
            sumsPath,
            string.Join('\n', lines) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
