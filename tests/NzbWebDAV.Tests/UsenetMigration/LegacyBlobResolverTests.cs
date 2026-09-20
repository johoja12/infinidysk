using NzbDavMigration.Legacy;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class LegacyBlobResolverTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"legacy-blobs-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadNzbAsync_RejectsDtdAndEnforcesAggregateCeiling()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        WriteBlob(first, "<!DOCTYPE nzb [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><nzb>&xxe;</nzb>");
        WriteBlob(second, "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />");

        var resolver = new LegacyBlobResolver(_root, perFileLimit: 1024, aggregateLimit: 100);
        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(() => resolver.ReadNzbAsync(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => resolver.ReadNzbAsync(second));
    }

    [Fact]
    public async Task ReadNzbAsync_InlineFallbackUsesSameCeilingsAndDtdPolicyAndBlobWins()
    {
        var id = Guid.NewGuid();
        const string xml = "<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\" />";
        var resolved = await new LegacyBlobResolver(_root).ReadNzbAsync(id, xml);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(xml), resolved.Bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LegacyBlobResolver(_root, perFileLimit: 10).ReadNzbAsync(id, xml));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LegacyBlobResolver(_root, aggregateLimit: 10).ReadNzbAsync(id, xml));
        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(() => new LegacyBlobResolver(_root)
            .ReadNzbAsync(id, "<!DOCTYPE nzb [<!ENTITY private SYSTEM 'file:///etc/passwd'>]><nzb>&private;</nzb>"));
        WriteBlob(id, xml);
        var preferred = await new LegacyBlobResolver(_root).ReadNzbAsync(id, "invalid retained fallback");
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(xml), preferred.Bytes);
        WriteBlob(id, "not XML");
        await Assert.ThrowsAnyAsync<System.Xml.XmlException>(() => new LegacyBlobResolver(_root).ReadNzbAsync(id, xml));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadNzbAsync_RejectsSymlinkedShardEvenWithInlineFallback(int depth)
    {
        Directory.CreateDirectory(_root);
        var id = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var sourceRoot = Path.Join(_root, "blobs");
        var link = depth switch
        {
            0 => sourceRoot,
            1 => Path.Join(sourceRoot, "ab"),
            _ => Path.Join(sourceRoot, "ab", "ab"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, Path.Join(_root, "missing-target"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LegacyBlobResolver(sourceRoot).ReadNzbAsync(id, "<nzb />"));
    }

    [Fact]
    public async Task ReadNzbAsync_DoesNotFallbackWhenBlobPathIsADirectory()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(BlobPath(id));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LegacyBlobResolver(_root).ReadNzbAsync(id, "<nzb />"));
    }

    [Fact]
    public async Task ReadNzbAsync_RejectsSymlinkedBlob()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var target = Path.Join(_root, "target.nzb");
        Directory.CreateDirectory(_root);
        File.WriteAllText(target, "<nzb />");
        var blobPath = BlobPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        File.CreateSymbolicLink(blobPath, target);

        await Assert.ThrowsAsync<InvalidDataException>(() => new LegacyBlobResolver(_root).ReadNzbAsync(id));
    }

    private void WriteBlob(Guid id, string content)
    {
        var path = BlobPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string BlobPath(Guid id)
    {
        var compact = id.ToString("N");
        return Path.Join(_root, compact[..2], compact.Substring(2, 2), id.ToString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
