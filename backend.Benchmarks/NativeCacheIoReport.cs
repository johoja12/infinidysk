using System.Diagnostics;
using System.Text.Json;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Benchmarks;

/// <summary>Manual local-storage measurement; never opens production cache paths or a provider.</summary>
public static class NativeCacheIoReport
{
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        if (args.Length != 1 || args[0] != "--native-cache-io-report") return false;
        var root = Path.Combine(Path.GetTempPath(), "native-io-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new NativeCacheStore(Path.Combine(root, "catalogue.db"),
                [new() { Path = root, MinFreeBytes = 0 }]);
            var block = new byte[NativeCacheStore.BlockSize];
            new Random(45).NextBytes(block);
            var identity = new NativeCacheIdentity("benchmark", "fixed", 64L * 1024 * 1024);
            for (long offset = 0; offset < identity.Length; offset += block.Length)
                if (!await store.WriteBlockAsync(identity, offset, block).ConfigureAwait(false)) throw new IOException("Fixture write failed.");
            var results = new List<object>();
            foreach (var concurrency in new[] { 1, 4 })
            {
                long reads = 0;
                async Task WarmAsync()
                {
                    await using var stream = new NativeCachedStream(store, identity,
                        _ => throw new InvalidOperationException("Cached verification requested source bytes"), () => true, background: true)
                    {
                        BeforeCacheIo = (write, _) => { if (!write) Interlocked.Increment(ref reads); return Task.CompletedTask; }
                    };
                    await NativePrefetchExecutor.WarmAsync(store, stream, 0, 0, _ => false, _ => { }, CancellationToken.None).ConfigureAwait(false);
                }
                var watch = Stopwatch.StartNew();
                for (var repeat = 0; repeat < 3; repeat++)
                    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => WarmAsync())).ConfigureAwait(false);
                results.Add(new { concurrency, requests = concurrency * 3, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    verifiedReadBytes = reads * block.Length, unsharedReadBytes = identity.Length * concurrency * 3, providerBytes = 0 });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { scope = "64 MiB disposable local fixture, warm OS page cache; not NAS throughput", results }));
        }
        finally { Directory.Delete(root, recursive: true); }
        return true;
    }
}
