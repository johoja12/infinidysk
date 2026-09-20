using NzbDavMigration.Export;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class CanaryPackageWriterTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"nzbdav-export-{Guid.NewGuid():N}");

    [Fact]
    public async Task WriteAsync_AtomicallyCreatesChecksummedPackageWithoutSecrets()
    {
        Directory.CreateDirectory(_root);
        var payload = Path.Join(_root, "source.nzb");
        await File.WriteAllTextAsync(payload, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />");
        var output = Path.Join(_root, "package");
        var leafId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var blobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var request = new CanaryExportRequest(
            "canary-test",
            output,
            [new CanaryExportRelease("release-1", blobId, payload,
                [new NzbDavExportLeaf(leafId, "/content/tv/episode.mkv", 123, "release-1", null,
                    blobId, NzbDavArticleIdentity.DirectKind, new string('a', 64), "ready", null)])],
            [new NzbDavSelectedLibraryLink("TV/episode.mkv", "/mnt/nzbdav/.ids/x", leafId)]);

        await new CanaryPackageWriter(1, 50).WriteAsync(request);

        Assert.True(File.Exists(Path.Join(output, "manifest.json")));
        Assert.True(File.Exists(Path.Join(output, "SHA256SUMS")));
        Assert.True(File.Exists(Path.Join(output, "payloads", "release-1.nzb")));
        var manifest = NzbDavExportManifestJson.Deserialize(
            await File.ReadAllTextAsync(Path.Join(output, "manifest.json")));
        Assert.Equal("canary-test", manifest.PackageId);
        Assert.DoesNotContain("password", await File.ReadAllTextAsync(Path.Join(output, "manifest.json")),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", await File.ReadAllTextAsync(Path.Join(output, "SHA256SUMS")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_RefusesOverwriteAndInvalidSelectionBounds()
    {
        Directory.CreateDirectory(_root);
        var output = Path.Join(_root, "existing");
        Directory.CreateDirectory(output);
        var request = new CanaryExportRequest("x", output, [], []);

        await Assert.ThrowsAsync<IOException>(() => new CanaryPackageWriter(1, 50).WriteAsync(request));
        Directory.Delete(output);
        await Assert.ThrowsAsync<InvalidDataException>(() => new CanaryPackageWriter(20, 50).WriteAsync(request));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
