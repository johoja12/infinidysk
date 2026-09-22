using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using NzbDavMigration.Export;
using NzbWebDAV.Config;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavScanRunnerTests : IDisposable
{
    private const string StandardNzbDoctype =
        "<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\">";
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-scan-{Guid.NewGuid():N}");

    [Fact]
    public async Task ScanAsync_AcceptsStandardExternalNzbDoctype()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var package = await CreatePackageAsync(doctype: StandardNzbDoctype);
        await harness.Store.UpdateSessionAsync(session =>
        {
            session.Status = MigrationSessionStatus.Scanning;
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = package;
        });
        await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-tv", "migrate");

        var summary = await new NzbDavScanRunner(harness.Store, new ConfigManager(), new NzbDavPackageReader())
            .ScanAsync();

        Assert.Equal(1, summary!.GreenCount);
        Assert.Equal(0, summary.RedCount);
    }

    [Theory]
    [InlineData("<!DOCTYPE nzb SYSTEM \"https://example.invalid/not-the-nzb-dtd\">")]
    [InlineData("<!DOCTYPE nzb [<!ENTITY unused SYSTEM \"file:///etc/passwd\">]>")]
    [InlineData("<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\" [<!ENTITY unused \"value\">]>")]
    [InlineData("<!DOCTYPE nzb PUBLIC \"-//newzBin//DTD NZB 1.1//EN\" \"http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd\" [ ]>")]
    public async Task ScanAsync_RejectsNonstandardAndInternalDoctypes(string doctype)
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var package = await CreatePackageAsync(doctype: doctype);
        await harness.Store.UpdateSessionAsync(session =>
        {
            session.Status = MigrationSessionStatus.Scanning;
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = package;
        });
        await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-tv", "migrate");

        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(() =>
            new NzbDavScanRunner(harness.Store, new ConfigManager(), new NzbDavPackageReader()).ScanAsync());
    }

    [Fact]
    public async Task ScanAsync_PersistsPackageRowsAndPendingSubmission()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var package = await CreatePackageAsync();
        await harness.Store.UpdateSessionAsync(session =>
        {
            session.Status = MigrationSessionStatus.Scanning;
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = package;
        });
        await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-tv", "migrate");
        var runner = new NzbDavScanRunner(harness.Store, new ConfigManager(), new NzbDavPackageReader());

        var summary = await runner.ScanAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.GreenCount);
        await using var db = harness.Mig();
        var release = await db.Releases.SingleAsync();
        var file = await db.ReleaseFiles.SingleAsync();
        Assert.StartsWith("nzbdav:", release.StoreRef, StringComparison.Ordinal);
        Assert.Equal("release-1.nzb", release.SubmitFileName);
        Assert.Equal("release-1.nzb", release.QueueFileName);
        Assert.Equal("release-1", release.JobName);
        Assert.Equal("migration-tv", release.TargetCategory);
        Assert.Equal(NzbDavArticleIdentity.DirectKind, file.ArticleIdentityKind);
        Assert.NotNull(file.SourceFileId);
        Assert.Equal("pending", (await db.Submissions.SingleAsync()).State);
        Assert.Equal(MigrationSessionStatus.Scanned, (await db.SessionState.SingleAsync()).Status);
    }

    [Fact]
    public async Task ScanAsync_PreservesExcludedLeafAsRedArtifact()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var package = await CreatePackageAsync(excluded: true);
        await harness.Store.UpdateSessionAsync(session =>
        {
            session.Status = MigrationSessionStatus.Scanning;
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = package;
        });
        await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-tv", "migrate");

        var summary = await new NzbDavScanRunner(harness.Store, new ConfigManager(), new NzbDavPackageReader())
            .ScanAsync();

        Assert.Equal(1, summary!.RedCount);
        await using var db = harness.Mig();
        Assert.Equal("red", (await db.Releases.SingleAsync()).Verdict);
        Assert.Empty(db.Submissions);
        Assert.NotEmpty(db.ScanErrors);
    }

    [Fact]
    public async Task ScanAsync_UsesSourceNamesInsteadOfUuidPayloadName()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        var sourceReleaseId = Guid.NewGuid().ToString();
        var package = await CreatePackageAsync(
            sourceReleaseId: sourceReleaseId,
            sourceFileName: "Outlander.Blood.of.My.Blood.S02E01.nzb",
            sourceJobName: "Outlander.Blood.of.My.Blood.S02E01");
        await harness.Store.UpdateSessionAsync(session =>
        {
            session.Status = MigrationSessionStatus.Scanning;
            session.SourceType = MigrationSourceTypes.NzbDav;
            session.SourcePackageRoot = package;
        });
        await harness.Store.SetCategoryMappingAsync("Migration-TV", "migration-tv", "migrate");

        await new NzbDavScanRunner(harness.Store, new ConfigManager(), new NzbDavPackageReader())
            .ScanAsync();

        await using var db = harness.Mig();
        var release = await db.Releases.SingleAsync();
        Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb", release.SubmitFileName);
        Assert.Equal("Outlander.Blood.of.My.Blood.S02E01.nzb", release.QueueFileName);
        Assert.Equal("Outlander.Blood.of.My.Blood.S02E01", release.JobName);
    }

    private async Task<string> CreatePackageAsync(
        bool excluded = false,
        string? doctype = null,
        string sourceReleaseId = "release-1",
        string? sourceFileName = null,
        string? sourceJobName = null)
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Join(_root, $"source-{Guid.NewGuid():N}.nzb");
        await File.WriteAllTextAsync(payload, $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            {{doctype ?? ""}}
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="sample"><segments><segment bytes="10" number="1">one@test</segment></segments></file>
            </nzb>
            """);
        var package = Path.Join(_root, $"package-{Guid.NewGuid():N}");
        var leafId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "scan-package",
            package,
            [new CanaryExportRelease(sourceReleaseId, blobId, payload,
                [new NzbDavExportLeaf(leafId, "/content/a.mkv", 10, sourceReleaseId, null, blobId,
                    NzbDavArticleIdentity.DirectKind, new string('a', 64), "ready", null)],
                SourceFileName: sourceFileName,
                SourceJobName: sourceJobName)],
            [new NzbDavSelectedLibraryLink("Migration-TV/a.mkv", "/legacy/.ids/a", leafId)]));
        if (excluded)
        {
            var manifestPath = Path.Join(package, "manifest.json");
            var manifest = NzbDavExportManifestJson.Deserialize(await File.ReadAllTextAsync(manifestPath));
            var leaf = manifest.Releases[0].Leaves[0] with
            {
                IdentityKind = "unavailable",
                IdentityDigest = null,
                ExtractionStatus = "excluded",
                ExclusionReason = "missing-identity",
            };
            manifest = manifest with
            {
                Releases = [manifest.Releases[0] with { Leaves = [leaf] }],
            };
            await File.WriteAllTextAsync(manifestPath, NzbDavExportManifestJson.Serialize(manifest));
            await using var stream = File.OpenRead(manifestPath);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            var sumsPath = Path.Join(package, "SHA256SUMS");
            var sums = (await File.ReadAllLinesAsync(sumsPath)).Select(line =>
                line.EndsWith("  manifest.json", StringComparison.Ordinal)
                    ? $"{digest}  manifest.json"
                    : line);
            await File.WriteAllTextAsync(sumsPath, string.Join('\n', sums) + "\n");
        }
        return package;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
