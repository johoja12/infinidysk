using System.Security.Cryptography;
using System.Text;
using MemoryPack;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavIdentityFeasibilityTests
{
    [Fact]
    public async Task DirectImport_RetainsReleaseBlobAndOrderedLeafSegments()
    {
        var fixture = await ReadFixtureAsync("direct-sample.nzb");
        var source = await ParseAsync(fixture);
        var releaseId = Guid.NewGuid();
        await using var context = CreateContext();
        var mount = CreateMount(releaseId, "Canary.Direct");
        context.Items.Add(mount);

        new FileAggregator(new DavDatabaseClient(context), mount, checkedFullHealth: false)
            .UpdateDatabase(
            [
                new FileProcessor.Result
                {
                    NzbFile = Assert.Single(source.Files),
                    FileName = "Canary.Direct.mkv",
                    FileSize = 220,
                    ReleaseDate = DateTimeOffset.UnixEpoch,
                },
            ]);

        var leaf = Assert.Single(context.Items.Local, item =>
            item.Type == DavItem.ItemType.UsenetFile);
        var persisted = RoundTrip(Assert.Single(context.BlobNzbFiles));
        var sourceByBlobId = new Dictionary<Guid, byte[]> { [releaseId] = fixture };
        var reloadedSource = await ParseAsync(sourceByBlobId[leaf.NzbBlobId!.Value]);

        // Target evidence: DavItem.NzbBlobId selects the immutable source NZB;
        // DavNzbFile.SegmentIds identifies the exact direct leaf within it.
        Assert.Equal(releaseId, leaf.NzbBlobId);
        Assert.Equal(
            source.Files[0].Segments.Select(segment => segment.MessageId),
            persisted.SegmentIds);
        Assert.Equal(ReleaseDigest(source), ReleaseDigest(reloadedSource));
    }

    [Fact]
    public async Task ArchiveImport_RetainsReleaseBlobOrderedPartsInnerPathAndSize()
    {
        var fixture = await ReadFixtureAsync("archive-sample.nzb");
        var source = await ParseAsync(fixture);
        var releaseId = Guid.NewGuid();
        await using var context = CreateContext();
        var mount = CreateMount(releaseId, "Canary.Archive");
        context.Items.Add(mount);

        var storedParts = source.Files.Select((file, index) =>
            new RarProcessor.StoredFileSegment
            {
                NzbFile = file,
                ArchiveSetId = "set:canary",
                PartSize = 20,
                ArchiveName = "Canary.Archive",
                PartNumber = new RarProcessor.PartNumber
                {
                    PartNumberFromHeader = index,
                    PartNumberFromFilename = index + 1,
                },
                ReleaseDate = DateTimeOffset.UnixEpoch,
                PathWithinArchive = "Feature/Canary.Archive.mkv",
                ByteRangeWithinPart = LongRange.FromStartAndSize(0, 8),
                AesParams = null,
                FileUncompressedSize = 16,
            }).ToArray();

        new RarAggregator(new DavDatabaseClient(context), mount, checkedFullHealth: false)
            .UpdateDatabase(
            [
                new RarProcessor.Result { StoredFileSegments = storedParts },
            ]);

        var leaf = Assert.Single(context.Items.Local, item =>
            item.Type == DavItem.ItemType.UsenetFile);
        var persisted = RoundTrip(Assert.Single(context.BlobMultipartFiles));
        var sourceByBlobId = new Dictionary<Guid, byte[]> { [releaseId] = fixture };
        var reloadedSource = await ParseAsync(sourceByBlobId[leaf.NzbBlobId!.Value]);

        // Target evidence: DavItem.NzbBlobId recovers the release article order;
        // multipart FileParts retain contributing segments, while DavItem.Path and
        // FileSize retain the archive member discriminator used by correlation.
        Assert.Equal(releaseId, leaf.NzbBlobId);
        Assert.Equal("/content/Canary.Archive/Feature/Canary.Archive.mkv", leaf.Path);
        Assert.Equal(16, leaf.FileSize);
        Assert.Equal(
            source.Files.SelectMany(file => file.Segments).Select(segment => segment.MessageId),
            persisted.Metadata.FileParts.SelectMany(part => part.SegmentIds));
        Assert.Equal(ReleaseDigest(source), ReleaseDigest(reloadedSource));
    }

    private static DavDatabaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new DavDatabaseContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private static DavItem CreateMount(Guid releaseId, string name) => DavItem.New(
        Guid.NewGuid(),
        DavItem.ContentFolder,
        name,
        null,
        DavItem.ItemType.Directory,
        DavItem.ItemSubType.Directory,
        null,
        null,
        historyItemId: releaseId,
        fileBlobId: null);

    private static async Task<byte[]> ReadFixtureAsync(string name) =>
        await File.ReadAllBytesAsync(Path.Join(
            AppContext.BaseDirectory,
            "Fixtures",
            "UsenetMigration",
            name));

    private static async Task<NzbDocument> ParseAsync(byte[] bytes) =>
        await NzbDocument.LoadAsync(new MemoryStream(bytes, writable: false));

    private static DavNzbFile RoundTrip(DavNzbFile file) =>
        MemoryPackSerializer.Deserialize<DavNzbFile>(
            MemoryPackSerializer.Serialize(file))!;

    private static DavMultipartFile RoundTrip(DavMultipartFile file) =>
        MemoryPackSerializer.Deserialize<DavMultipartFile>(
            MemoryPackSerializer.Serialize(file))!;

    private static string ReleaseDigest(NzbDocument document)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in document.Files)
        {
            Append(hash, "file");
            foreach (var segment in file.Segments)
            {
                Append(hash, segment.Number?.ToString() ?? "");
                Append(hash, segment.Bytes.ToString());
                Append(hash, segment.MessageId.Trim().Trim('<', '>'));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
