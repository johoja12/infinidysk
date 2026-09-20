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
        var originalBytes = await File.ReadAllBytesAsync(payload);
        var source = request.Releases[0];
        var frozenSource = System.Text.Json.JsonSerializer.Deserialize<CanaryExportRelease>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                source.SourceReleaseId, source.NzbBlobId, source.PayloadSourcePath, source.Leaves,
                PayloadBytes = originalBytes,
            }))!;
        request = request with { Releases = [frozenSource] };
        await File.WriteAllTextAsync(payload, "<nzb>replaced-after-validation</nzb>");

        await new CanaryPackageWriter(1, 50).WriteAsync(request);

        Assert.True(File.Exists(Path.Join(output, "manifest.json")));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(output));
        Assert.True(File.Exists(Path.Join(output, "SHA256SUMS")));
        Assert.True(File.Exists(Path.Join(output, "payloads", "release-1.nzb")));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(Path.Join(output, "payloads", "release-1.nzb")));
        if (!OperatingSystem.IsWindows())
            foreach (var path in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        var manifest = NzbDavExportManifestJson.Deserialize(
            await File.ReadAllTextAsync(Path.Join(output, "manifest.json")));
        Assert.Equal("canary-test", manifest.PackageId);
        Assert.DoesNotContain("password", await File.ReadAllTextAsync(Path.Join(output, "manifest.json")),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manifest.json", await File.ReadAllTextAsync(Path.Join(output, "SHA256SUMS")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryOutput_CreatesPrivateFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Join(_root, "inventory.json");
        using var stream = CanaryPackageWriter.CreatePrivateFile(path);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
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
