using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

/// <summary>Blocks of one entry commit in parallel without losing durability, accounting or exclusion.</summary>
public sealed class NativeCacheParallelCommitTests : IDisposable
{
    private const int Block = NativeCacheStore.BlockSize;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-parallel-" + Guid.NewGuid().ToString("N"));

    private NativeCacheFolder Folder()
    {
        var path = Path.Combine(_root, "cache");
        Directory.CreateDirectory(path);
        return new() { Id = "cache", Path = path, MinFreeBytes = 0 };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task ConcurrentBlocksOfOneEntry_AllPublish_AndJournalRecoversTheSameCoverage(int chunkSizeMb)
    {
        var folder = Folder();
        var identity = new NativeCacheIdentity("parallel-" + chunkSizeMb, "v1", 12L * Block + 12345);
        var blocks = Blocks(identity);
        var active = 0;
        var peak = 0;
        await using (var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder], chunkSizeMb)
        {
            BeforeWriteReserveAsync = async ct =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref peak, now);
                await Task.Delay(30, ct);
                Interlocked.Decrement(ref active);
            }
        })
        {
            var results = await Task.WhenAll(blocks.Select(block =>
                Task.Run(() => store.WriteBlockAsync(identity, block.Offset, block.Data, waitForWriter: true))));
            Assert.All(results, result => Assert.True(result));
            Assert.True(peak > 1, $"Blocks of one entry committed serially (peak {peak}).");
            Assert.Equal(identity.Length, await store.GetCoverageAsync(identity));
            await AssertReadsAsync(store, identity, blocks);
            Assert.Equal(0, PendingBytes(Path.Combine(_root, "index.db"), identity.Key));
        }

        var records = JournalRecords(folder, identity);
        Assert.Equal(blocks.Length, records.Count);
        Assert.Equal(blocks.Select(block => block.Offset).Order(), records.Select(record => record.Offset).Order());
        Assert.All(records, record => Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(blocks.Single(block => block.Offset == record.Offset).Data)),
            record.Hash));

        // Losing the local catalogue: an explicit scan rebuilds identical coverage from the journal.
        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder], chunkSizeMb);
        Assert.Equal(1, await recovered.ScanAsync(folder.Id));
        Assert.Equal(identity.Length, await recovered.GetCoverageAsync(identity));
        await AssertReadsAsync(recovered, identity, blocks);
    }

    [Fact]
    public async Task SameBlockHandedOverTwice_PublishesOneJournalRecord()
    {
        var folder = Folder();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeWriteReserveAsync = _ => release.Task,
        };
        var identity = new NativeCacheIdentity("duplicate", "v1", 2L * Block);
        var data = Blocks(identity)[1].Data;
        var first = store.WriteBlockAsync(identity, Block, data, waitForWriter: true);
        var second = store.WriteBlockAsync(identity, Block, data, waitForWriter: true);
        release.SetResult();
        Assert.True(await first);
        Assert.True(await second);
        Assert.Single(JournalRecords(folder, identity));
    }

    [Fact]
    public async Task ConcurrentCommits_RespectTheFolderQuota()
    {
        var folder = Folder() with { MaxBytes = 3L * (Block + 65536 + 4096) + 64 * 1024 };
        var catalogue = Path.Combine(_root, "index.db");
        await using var store = new NativeCacheStore(catalogue, [folder])
        {
            BeforeWriteReserveAsync = ct => Task.Delay(20, ct),
        };
        var identity = new NativeCacheIdentity("quota", "v1", 8L * Block);
        var blocks = Blocks(identity);
        var results = await Task.WhenAll(blocks.Select(block =>
            Task.Run(() => store.WriteBlockAsync(identity, block.Offset, block.Data, waitForWriter: true))));
        Assert.Contains(true, results);
        Assert.Equal(results.Count(result => result) * (long)Block, await store.GetCoverageAsync(identity));
        Assert.True(FolderBytes(catalogue, folder.Id) <= folder.MaxBytes);
        await AssertReadsAsync(store, identity, blocks.Where((_, index) => results[index]).ToArray());
    }

    [Fact]
    public async Task ConcurrentCommits_WithScansAndEviction_NeverPublishCorruptCoverage()
    {
        var folder = Folder();
        var catalogue = Path.Combine(_root, "index.db");
        var identity = new NativeCacheIdentity("churn", "v1", 16L * Block + 777);
        var blocks = Blocks(identity);
        await using (var store = new NativeCacheStore(catalogue, [folder], chunkSizeMb: 8)
        {
            BeforeWriteReserveAsync = ct => Task.Delay(Random.Shared.Next(0, 15), ct),
        })
        {
            var other = new NativeCacheIdentity("evictable", "v1", 3);
            using var stop = new CancellationTokenSource();
            var maintenance = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await store.WriteBlockAsync(other, 0, new byte[] { 1, 2, 3 });
                    await store.EvictAsync(folder.Id, clear: true);
                    await store.ScanAsync(folder.Id);
                    await store.ProcessOneCheckpointAsync();
                }
            });
            for (var attempt = 0; attempt < 20 && await store.GetCoverageAsync(identity) < identity.Length; attempt++)
            {
                await Task.WhenAll(blocks.Select(block =>
                    Task.Run(() => store.WriteBlockAsync(identity, block.Offset, block.Data, waitForWriter: true))));
                // Every block the catalogue reports must verify, whatever ran alongside it.
                await AssertCoveredBlocksVerifyAsync(store, identity, blocks);
            }
            await stop.CancelAsync();
            await maintenance;
            // Finish without churn so the final state is complete.
            foreach (var block in blocks) Assert.True(await store.WriteBlockAsync(identity, block.Offset, block.Data, waitForWriter: true));
            Assert.Equal(identity.Length, await store.GetCoverageAsync(identity));
            await AssertReadsAsync(store, identity, blocks);
        }

        await using var recovered = new NativeCacheStore(Path.Combine(_root, "recovered.db"), [folder], chunkSizeMb: 8);
        await recovered.ScanAsync(folder.Id);
        Assert.Equal(identity.Length, await recovered.GetCoverageAsync(identity));
        await AssertReadsAsync(recovered, identity, blocks);
    }

    private static (long Offset, byte[] Data)[] Blocks(NativeCacheIdentity identity)
    {
        var random = new Random(identity.Key.GetHashCode(StringComparison.Ordinal));
        return Enumerable.Range(0, (int)((identity.Length + Block - 1) / Block)).Select(index =>
        {
            var offset = (long)index * Block;
            var data = new byte[Math.Min(Block, identity.Length - offset)];
            random.NextBytes(data);
            return (offset, data);
        }).ToArray();
    }

    private static async Task AssertReadsAsync(NativeCacheStore store, NativeCacheIdentity identity, (long Offset, byte[] Data)[] blocks)
    {
        var buffer = new byte[Block];
        foreach (var (offset, data) in blocks)
        {
            Assert.Equal(data.Length, await store.ReadBlockAsync(identity, offset, buffer));
            Assert.True(buffer.AsSpan(0, data.Length).SequenceEqual(data), $"Block {offset} differs.");
        }
    }

    private static async Task AssertCoveredBlocksVerifyAsync(NativeCacheStore store, NativeCacheIdentity identity,
        (long Offset, byte[] Data)[] blocks)
    {
        var ranges = await store.ListVerifiedRangesAsync(identity.Key, -1, 100);
        var buffer = new byte[Block];
        foreach (var range in ranges)
        {
            var data = blocks.Single(block => block.Offset == range.Offset).Data;
            var read = await store.ReadBlockAsync(identity, range.Offset, buffer);
            // A concurrent eviction may remove the entry between listing and reading; it must never return wrong bytes.
            if (read != 0) Assert.True(buffer.AsSpan(0, data.Length).SequenceEqual(data), $"Block {range.Offset} differs.");
        }
    }

    private static List<(long Offset, int Count, string Hash)> JournalRecords(NativeCacheFolder folder, NativeCacheIdentity identity)
    {
        var path = Path.Combine(folder.Path, "v1", identity.Key[..2], identity.Key, "ranges.journal");
        return File.ReadAllLines(path).Select(line =>
        {
            using var record = JsonDocument.Parse(line);
            return (record.RootElement.GetProperty("Offset").GetInt64(), record.RootElement.GetProperty("Count").GetInt32(),
                record.RootElement.GetProperty("Hash").GetString()!);
        }).ToList();
    }

    private static long PendingBytes(string catalogue, string key) =>
        Scalar(catalogue, "SELECT PendingBytes FROM Entries WHERE Key=$key", ("$key", key));

    private static long FolderBytes(string catalogue, string folder) =>
        Scalar(catalogue, "SELECT Bytes FROM FolderTotals WHERE Folder=$key", ("$key", folder));

    private static long Scalar(string catalogue, string sql, (string Name, string Value) parameter)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = catalogue, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (long)command.ExecuteScalar()!;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
