using System.Diagnostics;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using Xunit.Abstractions;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class ShardedMappedInventoryScaleTests(ITestOutputHelper testOutput)
{
    [SkippableFact]
    public async Task ProductionScaleFixture_UsesBoundedResidentMemory()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_MIGRATION_SCALE_TEST") == "1",
            "Opt-in 41,446-row migration memory fixture.");
        const int rowCount = 41_446;
        var fixture = Path.Join(Path.GetTempPath(), $"mapped-scale-{Guid.NewGuid():N}");
        var source = Path.Join(fixture, "library");
        var ids = Path.Join(fixture, ".ids");
        var blobs = Path.Join(fixture, "blobs");
        var output = Path.Join(fixture, "output");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(ids);
        Directory.CreateDirectory(blobs);
        try
        {
            var links = new List<LegacyLocalLinkRow>(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var id = Guid.NewGuid();
                var path = Path.Join(source, $"{index:D6}.mkv");
                File.CreateSymbolicLink(path, Path.Join(ids, id.ToString()));
                links.Add(new LegacyLocalLinkRow(path, id, false));
            }
            var process = Process.GetCurrentProcess();
            var peak = process.WorkingSet64;
            using var cancellation = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    process.Refresh();
                    peak = Math.Max(peak, process.WorkingSet64);
                    await Task.Delay(20, cancellation.Token);
                }
            });
            ShardedMappedInventoryManifest manifest;
            try
            {
                manifest = await new ShardedMappedInventoryWriter(new SyntheticReader(links))
                    .WriteAsync(source, ids, blobs, output, batchSize: 64,
                        connectionString: "synthetic");
            }
            finally
            {
                cancellation.Cancel();
                try { await sampler; }
                catch (OperationCanceledException) { }
            }
            Assert.Equal(rowCount, manifest.RowCount);
            Assert.Equal(648, manifest.Shards.Count);
            testOutput.WriteLine($"Peak test-process RSS: {peak / (1024 * 1024)} MiB");
            Assert.True(peak < 1024L * 1024 * 1024,
                $"Resident memory exceeded 1 GiB: {peak / (1024 * 1024)} MiB");
        }
        finally
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        }
    }

    private sealed class SyntheticReader(IReadOnlyList<LegacyLocalLinkRow> links) : ILegacyMappedBatchReader
    {
        public async Task ReadMappedBatchesAsync(
            string connectionString,
            string libraryRoot,
            Func<IReadOnlyList<LegacyLocalLinkRow>, LegacyMappedReadResult, Task> onBatch,
            int batchSize = 128,
            CancellationToken cancellationToken = default)
        {
            foreach (var batch in links.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rows = batch.Select(link => new LegacyDavItemRow(
                    link.DavItemId, $"/content/{link.DavItemId}.mkv", 100, 3,
                    null, null, NzbSegmentsJson: new string('x', 32 * 1024),
                    HistoryExclusion: "missing-history")).ToArray();
                await onBatch(links, new LegacyMappedReadResult(batch, rows, [])).ConfigureAwait(false);
            }
        }
    }
}
