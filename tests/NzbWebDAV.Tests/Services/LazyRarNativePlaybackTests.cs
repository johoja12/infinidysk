using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using static NzbWebDAV.Tests.Fakes.Rar4TestArchiveBuilder;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Regression coverage for #111/#114: lazy RAR volume resolution persists the multipart
/// blob while a Native Cache response is streaming it. That write fills in metadata only,
/// so the response must keep playing and the cache identity must not change; a genuine
/// source change must still fail the response closed.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class LazyRarNativePlaybackTests : IDisposable
{
    private const string PathInArchive = "movie.mkv";
    private const int MiB = 1024 * 1024;
    // The first volume covers the whole first 4 MiB cache block; later volumes resolve
    // (and persist) only after the response has served bytes.
    private const int FirstVolumeData = 5 * MiB;
    private const int LaterVolumeData = 2 * MiB;
    private const long FileSize = FirstVolumeData + 2L * LaterVolumeData;

    private readonly string _root = Path.Join(Path.GetTempPath(), $"lazy-rar-native-{Guid.NewGuid():N}");
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
    private readonly FileBlobStore _blobs;
    private readonly RepairPatchStore _repairs;
    private readonly FakeNntpClient _client;
    // Holds the stream's background pre-warm of later volumes until the test releases it.
    private readonly TaskCompletionSource _resolutionGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _expected = Enumerable.Range(0, (int)FileSize)
        .Select(index => (byte)((index * 31 + 7) % 251)).ToArray();

    public LazyRarNativePlaybackTests()
    {
        Directory.CreateDirectory(Path.Join(_root, "media"));
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        _blobs = new FileBlobStore();
        BlobStore.Use(_blobs);
        _repairs = new RepairPatchStore(Path.Join(_root, "patches"), 100);
        _client = new FakeNntpClient(new Dictionary<string, byte[]>
        {
            ["vol1"] = _expected[..FirstVolumeData],
            ["vol2"] = Continuation(1, splitAfter: true),
            ["vol3"] = Continuation(2, splitAfter: false),
        }, useCachedYencStreams: true);
    }

    public void Dispose()
    {
        _resolutionGate.TrySetResult();
        _client.Dispose();
        _repairs.Dispose();
        _blobs.Dispose();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _oldConfig);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task VolumesResolvedDuringActiveRead_DoNotInterruptPlaybackOrChangeIdentity()
    {
        var (item, resolver) = await ImportLazyAsync();
        await using var native = new NativeCacheService(Config(), _blobs, _repairs);
        Assert.True(await native.WaitForInitializationAsync());
        var before = await native.GetCurrentCacheIdentityAsync(item);
        Assert.NotNull(before);
        Assert.StartsWith("v3:", before!.Generation, StringComparison.Ordinal);

        await using var stream = await native.WrapAsync(item, ct => Open(item, resolver), CancellationToken.None);
        Assert.IsType<NativeCachedStream>(stream);

        // Serve bytes from the first block while every later volume is still pending.
        var actual = new byte[FileSize];
        var offset = await ReadAsync(stream, actual, 0, 64 * 1024);
        Assert.Equal(64 * 1024, offset);
        Assert.True((await _blobs.ReadBlob<DavMultipartFile>(item.FileBlobId!.Value))!.Metadata.IsLazy);

        // The stream's background pre-warm now resolves and persists the later volumes
        // mid-response, as in production (#114).
        _resolutionGate.TrySetResult();
        await WaitForPersistedResolutionAsync(item.FileBlobId!.Value);

        offset = await ReadAsync(stream, actual, offset, actual.Length - offset);
        Assert.Equal(FileSize, offset);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal(_expected, actual);

        var after = await native.GetCurrentCacheIdentityAsync(item);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task GenuineSourceChange_StillFailsServedResponse()
    {
        var (item, resolver) = await ImportLazyAsync();
        await using var native = new NativeCacheService(Config(), _blobs, _repairs);
        Assert.True(await native.WaitForInitializationAsync());
        var before = await native.GetCurrentCacheIdentityAsync(item);

        await using var stream = await native.WrapAsync(item, ct => Open(item, resolver), CancellationToken.None);
        Assert.IsType<NativeCachedStream>(stream);
        Assert.True(await stream.ReadAsync(new byte[1024]) > 0);

        // A repair or re-import replacing the source articles publishes a new revision.
        var replaced = await _blobs.ReadBlob<DavMultipartFile>(item.FileBlobId!.Value);
        replaced!.Metadata = new DavMultipartFile.Meta
        {
            PathInArchive = PathInArchive,
            ExpectedFileSize = FileSize,
            FileParts = [Part("vol1-repaired", 0, FirstVolumeData), .. replaced.Metadata.FileParts.Skip(1)],
            PendingParts = replaced.Metadata.PendingParts,
            IsLazy = replaced.Metadata.IsLazy,
        };
        await _blobs.WriteBlob(replaced.Id, replaced);

        await Assert.ThrowsAsync<MediaSourceChangedException>(
            () => stream.ReadAsync(new byte[1024]).AsTask());
        Assert.NotEqual(before, await native.GetCurrentCacheIdentityAsync(item));
    }

    private async Task<(DavItem Item, LazyRarResolver Resolver)> ImportLazyAsync()
    {
        var volumes = new Dictionary<string, byte[]>
        {
            ["vol2"] = Continuation(1, splitAfter: true),
            ["vol3"] = Continuation(2, splitAfter: false),
        };
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                PathInArchive = PathInArchive,
                ExpectedFileSize = FileSize,
                FileParts = [Part("vol1", 0, FirstVolumeData)],
                PendingParts = volumes.Select(volume => new DavMultipartFile.PendingPart
                {
                    SegmentIds = [volume.Key],
                    SegmentIdByteRange = LongRange.FromStartAndSize(0, volume.Value.Length),
                    EstimatedDataSize = LaterVolumeData,
                }).ToArray(),
            },
        };
        await _blobs.WriteBlob(multipart.Id, multipart);
        var item = new DavItem
        {
            Id = Guid.NewGuid(), Name = PathInArchive, FileSize = FileSize,
            FileBlobId = multipart.Id, SubType = DavItem.ItemSubType.MultipartFile,
        };
        var resolver = new LazyRarResolver(_client, new ConfigManager())
        {
            VolumeStreamFactory = (ids, _) => new GatedStream(volumes[ids[0]], _resolutionGate.Task),
            ReconcileFileSizeAsync = (_, _, _) => Task.CompletedTask,
        };
        return (item, resolver);
    }

    private async Task<Stream> Open(DavItem item, LazyRarResolver resolver)
    {
        var multipart = await _blobs.ReadBlob<DavMultipartFile>(item.FileBlobId!.Value);
        return new DavMultipartFileStream(multipart!, _client, 0, resolver,
            usePipelinedBodyRequests: false, fileName: PathInArchive);
    }

    private static async Task WaitForPersistedResolutionAsync(Guid blobId)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            using (var disk = new FileBlobStore())
            {
                if ((await disk.ReadBlob<DavMultipartFile>(blobId))?.Metadata is { IsLazy: false })
                    return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Lazy RAR resolution was never persisted.");
    }

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        var end = offset + count;
        while (offset < end)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, Math.Min(64 * 1024, end - offset)));
            if (read == 0) break;
            offset += read;
        }

        return offset;
    }

    private byte[] Continuation(int volumeIndex, bool splitAfter)
    {
        var volume = BuildRar4ContinuationVolume(PathInArchive, LaterVolumeData, splitAfter: splitAfter);
        _expected.AsSpan(FirstVolumeData + (volumeIndex - 1) * LaterVolumeData, LaterVolumeData)
            .CopyTo(volume.AsSpan(volume.Length - LaterVolumeData));
        return volume;
    }

    private static DavMultipartFile.FilePart Part(string segment, long start, long size) => new()
    {
        SegmentIds = [segment],
        SegmentIdByteRange = LongRange.FromStartAndSize(0, start + size),
        FilePartByteRange = LongRange.FromStartAndSize(start, size),
        IsSplitAfter = true,
    };

    private ConfigManager Config()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheMinFileMb, ConfigValue = "0" },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheMetadataPath, ConfigValue = Path.Join(_root, "index") },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] {
                new NativeCacheFolder { Id = "media", Path = Path.Join(_root, "media"), MinFreeBytes = 0 }
            }) }
        ]);
        return config;
    }

    // Volume header reads wait for the gate without blocking the opening thread.
    private sealed class GatedStream(byte[] data, Task gate) : MemoryStream(data, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            gate.Wait();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            gate.Wait();
            return base.Read(buffer);
        }
    }
}
