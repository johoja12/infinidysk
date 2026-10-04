using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

/// <summary>Health repair keeps a file whose whole content the native cache can still serve.</summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class CachedCopyCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cached-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");

    public CachedCopyCheckTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "media"));
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
    }

    [Fact]
    public async Task CheckCachedCopy_ReportsNotCachedPartialVerifiedAndDamaged()
    {
        using var blobs = new FileBlobStore();
        using var repairs = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using var native = new NativeCacheService(Config(), blobs, repairs);
        Assert.True(await native.WaitForInitializationAsync());
        var item = await NewItemAsync(blobs, NativeCacheStore.BlockSize + 3);

        Assert.Equal(CachedCopyState.NotCached, (await native.CheckCachedCopyAsync(item)).State);
        await SeedAsync(native, item, NativeCacheStore.BlockSize);
        Assert.Equal(CachedCopyState.Partial, (await native.CheckCachedCopyAsync(item)).State);
        await SeedAsync(native, item, (int)item.FileSize!.Value);
        var verified = await native.CheckCachedCopyAsync(item);
        Assert.Equal(CachedCopyState.Verified, verified.State);
        Assert.Equal(await native.GetCurrentCacheIdentityAsync(item), verified.Identity);

        // The head block is always sampled; corrupting it invalidates the block.
        var data = Directory.GetFiles(Path.Combine(_root, "media"), "*.data", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).First(); // content.data, or the first chunk of a chunked entry.
        await using (var file = new FileStream(data, FileMode.Open, FileAccess.Write))
            await file.WriteAsync(new byte[] { 9, 9, 9 });
        Assert.Equal(CachedCopyState.Damaged, (await native.CheckCachedCopyAsync(item)).State);
        Assert.Equal(CachedCopyState.Partial, (await native.CheckCachedCopyAsync(item)).State);
    }

    [Fact]
    public async Task Protection_KeepsManualPinsAndSurvivesEviction()
    {
        await using var store = new NativeCacheStore(Path.Combine(_root, "catalogue.db"),
            [new NativeCacheFolder { Id = "media", Path = Path.Combine(_root, "media"), MinFreeBytes = 0 }]);
        var kept = new NativeCacheIdentity(Guid.NewGuid().ToString("N"), "v1", 3);
        var manual = new NativeCacheIdentity(Guid.NewGuid().ToString("N"), "v1", 3);
        var other = new NativeCacheIdentity("other", "v1", 3);
        foreach (var identity in new[] { kept, manual, other })
            Assert.True(await store.WriteBlockAsync(identity, 0, new byte[] { 1, 2, 3 }));
        await store.SetPinnedAsync(manual, true);

        await store.ProtectAsync(kept);
        await store.ProtectAsync(manual);

        Assert.Equal(kept.Key, Assert.Single(await store.ListProtectedAsync(10)).Key);
        Assert.Equal(1, await store.EvictAsync("media", clear: true));
        Assert.Equal(2, (await store.BrowseFilesAsync(null, null, "all", "recent", null, 10)).Items.Count);

        await store.ReleaseProtectionAsync([kept.Key, manual.Key]);
        Assert.Empty(await store.ListProtectedAsync(10));
        Assert.Equal(1, await store.EvictAsync("media", clear: true)); // Manual pin stays.
    }

    [Fact]
    public async Task Maintenance_ReleasesProtectionForDeletedItems()
    {
        using var blobs = new FileBlobStore();
        using var repairs = new RepairPatchStore(Path.Combine(_root, "patches"), 100);
        await using var native = new NativeCacheService(Config(), blobs, repairs);
        Assert.True(await native.WaitForInitializationAsync());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using (var context = new DavDatabaseContext(options)) await context.Database.EnsureCreatedAsync();

        var live = await NewItemAsync(blobs, 3);
        var removed = await NewItemAsync(blobs, 3);
        await using (var context = new DavDatabaseContext(options))
        {
            context.Items.Add(live);
            await context.SaveChangesAsync();
        }
        foreach (var item in new[] { live, removed })
        {
            await SeedAsync(native, item, 3);
            var check = await native.CheckCachedCopyAsync(item);
            Assert.Equal(CachedCopyState.Verified, check.State);
            await native.Store!.ProtectAsync(check.Identity!);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new DavDatabaseContext(options));
        services.AddScoped(sp => new DavDatabaseClient(sp.GetRequiredService<DavDatabaseContext>(), blobs));
        await using var provider = services.BuildServiceProvider();
        var maintenance = new NativeCacheBrowserMaintenance(native, provider.GetRequiredService<IServiceScopeFactory>());

        Assert.Equal(1, await maintenance.ReleaseStaleProtectionAsync(native.Store!, CancellationToken.None));
        var liveKey = (await native.GetCurrentCacheIdentityAsync(live))!.Key;
        Assert.Equal(liveKey, Assert.Single(await native.Store!.ListProtectedAsync(10)).Key);
    }

    private static async Task<DavItem> NewItemAsync(FileBlobStore blobs, long size)
    {
        var blobId = Guid.NewGuid();
        await blobs.WriteBlob(blobId, new DavNzbFile { Id = blobId, SegmentIds = [Guid.NewGuid().ToString("N")] });
        var id = Guid.NewGuid();
        return new DavItem { Id = id, IdPrefix = id.ToString("N")[..5], Path = $"/{id:N}.mkv", Name = $"{id:N}.mkv",
            FileBlobId = blobId, FileSize = size, Type = DavItem.ItemType.UsenetFile, SubType = DavItem.ItemSubType.NzbFile };
    }

    private static async Task SeedAsync(NativeCacheService native, DavItem item, int bytes)
    {
        await using var stream = await native.WrapAsync(item,
            _ => Task.FromResult<Stream>(new CacheableStream(new byte[item.FileSize!.Value])), CancellationToken.None);
        var buffer = new byte[bytes];
        var read = 0;
        while (read < bytes)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read));
            if (count == 0) break;
            read += count;
        }
        Assert.Equal(bytes, read);
    }

    private ConfigManager Config()
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheMinFileMb, ConfigValue = "0" },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheMetadataPath, ConfigValue = Path.Combine(_root, "index") },
            new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] {
                new NativeCacheFolder { Id = "media", Path = Path.Combine(_root, "media"), MinFreeBytes = 0 }
            }) }
        ]);
        return config;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _oldConfig);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class CacheableStream(byte[] content) : MemoryStream(content), ICacheReadEvidence
    {
        public bool LastReadCacheable => true;
    }
}
