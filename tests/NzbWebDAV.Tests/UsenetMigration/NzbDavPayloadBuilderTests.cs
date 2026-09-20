using NzbDavMigration.Export;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavPayloadBuilderTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-payload-{Guid.NewGuid():N}");

    [Fact]
    public async Task BuildAsync_ReturnsOriginalBytesAfterSubmitTimeVerification()
    {
        var (package, expected) = await CreatePackageAsync();
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using var context = harness.Mig();
        var release = new MigrationRelease { StoreRef = "nzbdav:release-1" };
        var session = new MigrationSessionState { SourcePackageRoot = package };

        var actual = await new NzbDavPayloadBuilder(new NzbDavPackageReader())
            .BuildAsync(release, session, context);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task BuildAsync_RejectsPayloadChangedAfterScan()
    {
        var (package, _) = await CreatePackageAsync();
        await File.AppendAllTextAsync(Path.Join(package, "payloads", "release-1.nzb"), "tamper");
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using var context = harness.Mig();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new NzbDavPayloadBuilder(new NzbDavPackageReader()).BuildAsync(
                new MigrationRelease { StoreRef = "nzbdav:release-1" },
                new MigrationSessionState { SourcePackageRoot = package }, context));
    }

    private async Task<(string Package, byte[] Payload)> CreatePackageAsync()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Join(_root, $"source-{Guid.NewGuid():N}.nzb");
        var bytes = "<nzb><file subject=\"x\"><segments><segment bytes=\"1\" number=\"1\">x@test</segment></segments></file></nzb>"u8.ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        var package = Path.Join(_root, $"package-{Guid.NewGuid():N}");
        var leaf = Guid.NewGuid();
        var blob = Guid.NewGuid();
        await new CanaryPackageWriter(1, 50).WriteAsync(new CanaryExportRequest(
            "payload-test", package,
            [new CanaryExportRelease("release-1", blob, source,
                [new NzbDavExportLeaf(leaf, "/content/x.mkv", 1, "release-1", null, blob,
                    NzbDavArticleIdentity.DirectKind, new string('a', 64), "ready", null)])],
            [new NzbDavSelectedLibraryLink("Migration/x.mkv", "/legacy/x", leaf)]));
        return (package, bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
