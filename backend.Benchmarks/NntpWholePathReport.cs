using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Observability;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Requests;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Provider-independent whole-path NNTP report. The socket server is a child process
/// so yEnc encoding, TLS (when introduced), and server writes do not affect client CPU.
/// It is intentionally a measurement harness; it does not change the product path.
/// </summary>
internal static class NntpWholePathReport
{
    internal const string ReportName = "nntp-whole-path";
    internal const int CorpusSeed = 1025;
    internal const int DefaultResponseCopyChunkBytes = 64 * 1024;
    private const int TimedRepetitions = 3;

    public static async Task RunAsync(
        string? jsonPath,
        string scenarioSet,
        string? scenarioName = null)
    {
        var selected = NntpWholePathScenario.ForSet(scenarioSet)
            .Where(scenario => scenarioName is null ||
                               scenario.Name.Equals(scenarioName, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
            throw new ArgumentException($"No scenario named '{scenarioName}' exists in set '{scenarioSet}'.");

        var snapshots = new Dictionary<string, ScenarioSnapshot>(StringComparer.Ordinal);
        foreach (var scenario in selected)
        {
            // Tiered JIT and native dispatch setup are intentionally discarded.
            await ExecuteAsync(
                    scenario,
                    verifyHash: false,
                    httpLike: scenario.Layer == NntpWholePathLayer.HttpLike,
                    DefaultResponseCopyChunkBytes)
                .ConfigureAwait(false);
            var correctness = await ExecuteAsync(
                    scenario,
                    verifyHash: true,
                    httpLike: scenario.Layer == NntpWholePathLayer.HttpLike,
                    DefaultResponseCopyChunkBytes)
                .ConfigureAwait(false);

            NntpWholePathTiming? total = null;
            for (var repetition = 0; repetition < TimedRepetitions; repetition++)
            {
                var timed = await ExecuteAsync(
                    scenario,
                    verifyHash: false,
                    httpLike: scenario.Layer == NntpWholePathLayer.HttpLike,
                    DefaultResponseCopyChunkBytes)
                    .ConfigureAwait(false);
                total = total is null ? timed.Timing : Add(total, timed.Timing);
            }

            var timing = Divide(total!, TimedRepetitions);
            var deterministic = correctness.Deterministic;
            snapshots[scenario.Name] = new ScenarioSnapshot(
                new Dictionary<string, long>(StringComparer.Ordinal)
                {
                    ["expectedBytes"] = deterministic.ExpectedBytes,
                    ["actualBytes"] = deterministic.ActualBytes,
                    ["sha256Match"] = deterministic.Sha256Match,
                    ["bodyCommands"] = deterministic.BodyCommands,
                    ["responses"] = deterministic.Responses,
                    ["retrievedCallbacks"] = deterministic.RetrievedCallbacks,
                    ["cancelledCallbacks"] = deterministic.CancelledCallbacks,
                    ["notFoundCallbacks"] = deterministic.NotFoundCallbacks,
                    ["notRetrievedCallbacks"] = deterministic.NotRetrievedCallbacks,
                    ["finalArticleBudgetBytes"] = deterministic.FinalArticleBudgetBytes,
                },
                PerformanceReportJson.WholePathTiming(
                    timing.WallSeconds,
                    timing.ClientCpuSeconds,
                    timing.ServerCpuSeconds,
                    timing.ClientCpuSecondsPerGb,
                    timing.ThroughputMbps,
                    timing.TimeToPeakActiveMs,
                    timing.ClientAllocatedBytes,
                    timing.Gen0Collections,
                    timing.Gen1Collections,
                    timing.Gen2Collections));

            Console.WriteLine(
                $"{scenario.Name} bytes={deterministic.ActualBytes} sha256_match={deterministic.Sha256Match} " +
                $"body_commands={deterministic.BodyCommands} peak_connections={deterministic.PeakActiveConnections} " +
                $"time_to_peak_active_ms={timing.TimeToPeakActiveMs:F3} " +
                $"wall_s={timing.WallSeconds:F3} " +
                $"throughput_mb_s={timing.ThroughputMbps:F3} client_cpu_s={timing.ClientCpuSeconds:F3}");
        }

        if (jsonPath is not null)
            PerformanceReportJson.Write(jsonPath, ReportName, snapshots);
    }

    public static async Task RunChildServerAsync(LoopbackServerArguments arguments)
    {
        var corpus = NntpLoopbackCorpus.Create(arguments.ArticleCount, arguments.DecodedArticleBytes, arguments.Seed);
        await using var server = await NntpLoopbackServer.StartAsync(
                corpus,
                arguments.RoundTripDelayMs,
                arguments.BandwidthBytesPerSecond,
                arguments.HandshakeDelayMs,
                arguments.MissingIds)
            .ConfigureAwait(false);
        Console.WriteLine($"READY {server.Port}");
        await Console.Out.FlushAsync().ConfigureAwait(false);
        await Console.In.ReadLineAsync().ConfigureAwait(false);
        await server.WaitForIdleAsync(TimeSpan.FromSeconds(15), CancellationToken.None).ConfigureAwait(false);
        await server.WriteSnapshotAsync(arguments.CountersPath, CancellationToken.None).ConfigureAwait(false);
    }

    internal static Task<NntpWholePathResult> ExecuteForCopyChunkAsync(
        NntpWholePathScenario scenario,
        bool verifyHash,
        int responseCopyChunkBytes) =>
        ExecuteAsync(scenario, verifyHash, httpLike: true, responseCopyChunkBytes);

    private static async Task<NntpWholePathResult> ExecuteAsync(
        NntpWholePathScenario scenario,
        bool verifyHash,
        bool httpLike,
        int responseCopyChunkBytes)
    {
        if (scenario.UseTls)
            throw new NotSupportedException(
                "Validated TLS loopback scenarios are deferred until a test-CA trust mechanism is available.");

        var corpus = NntpLoopbackCorpus.Create(
            scenario.ArticleCount,
            scenario.DecodedArticleBytes,
            CorpusSeed);
        var countersPath = Path.Join(Path.GetTempPath(), $"nntp-loopback-{Guid.NewGuid():N}.json");
        await using var server = await LoopbackServerProcess.StartAsync(scenario, countersPath).ConfigureAwait(false);
        var callbackCounts = new CallbackCounts();
        var budget = new InFlightArticleBudget(corpus.ExpectedBytes + scenario.DecodedArticleBytes);
        var oldBudget = InFlightArticleBudget.Current;
        InFlightArticleBudget.Current = budget;
        try
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var collectionCounts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var startedTimestamp = Stopwatch.GetTimestamp();
            var started = Stopwatch.StartNew();
            var bytes = scenario.Layer switch
            {
                NntpWholePathLayer.Transport => await ReadTransportAsync(
                    scenario, server.Port, corpus, callbackCounts, verifyHash).ConfigureAwait(false),
                NntpWholePathLayer.Provider => await ReadProviderAsync(
                    scenario, server.Port, corpus, callbackCounts, verifyHash).ConfigureAwait(false),
                NntpWholePathLayer.BufferedStream or NntpWholePathLayer.HttpLike =>
                    await ReadBufferedStreamAsync(
                        scenario, server.Port, corpus, callbackCounts, budget, verifyHash, httpLike,
                        responseCopyChunkBytes, startedTimestamp)
                    .ConfigureAwait(false),
                NntpWholePathLayer.NativeCache => await ReadNativeCacheAsync(
                        scenario, server.Port, corpus, budget, verifyHash, responseCopyChunkBytes, startedTimestamp)
                    .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            started.Stop();
            if (bytes.AfterTiming is { } afterTiming) await afterTiming().ConfigureAwait(false);
            process.Refresh();

            var serverSnapshot = await server.StopAndGetSnapshotAsync().ConfigureAwait(false);
            var shaMatches = bytes.Sha256 is null || bytes.Sha256.Equals(corpus.ExpectedSha256, StringComparison.Ordinal);
            if (verifyHash && (!shaMatches || bytes.Count != corpus.ExpectedBytes))
                throw new InvalidOperationException(
                    $"Whole-path output did not match corpus: {bytes.Count}/{corpus.ExpectedBytes}, hash={bytes.Sha256}.");
            if (budget.LeasedBytes != 0)
                throw new InvalidOperationException($"Article budget leaked {budget.LeasedBytes} bytes.");

            var elapsed = Math.Max(started.Elapsed.TotalSeconds, double.Epsilon);
            var clientCpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
            var serverCpu = server.ProcessCpuSeconds;
            return new NntpWholePathResult(
                scenario,
                new NntpWholePathDeterministic(
                    corpus.ExpectedBytes,
                    bytes.Count,
                    shaMatches ? 1 : 0,
                    serverSnapshot.BodyCommands,
                    serverSnapshot.Responses,
                    callbackCounts.Retrieved,
                    callbackCounts.Cancelled,
                    callbackCounts.NotFound,
                    callbackCounts.NotRetrieved,
                    budget.LeasedBytes,
                    serverSnapshot.PeakActiveConnections),
                new NntpWholePathTiming(
                    started.Elapsed.TotalSeconds,
                    clientCpu,
                    serverCpu,
                    clientCpu / (bytes.Count / 1_000_000_000d),
                    bytes.Count / elapsed / 1_000_000d,
                    bytes.TimeToFirstByte?.TotalMilliseconds ?? 0,
                    serverSnapshot.TimeToPeakActiveMs,
                    GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
                    GC.CollectionCount(0) - collectionCounts[0],
                    GC.CollectionCount(1) - collectionCounts[1],
                    GC.CollectionCount(2) - collectionCounts[2]));
        }
        finally
        {
            InFlightArticleBudget.Current = oldBudget;
            File.Delete(countersPath);
        }
    }

    private static async Task<ReadResult> ReadTransportAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        bool verifyHash)
    {
        await using var client = new UsenetClient(CreateOptions(scenario));
        await client.ConnectAsync("127.0.0.1", port, useSsl: false, CancellationToken.None).ConfigureAwait(false);
        await client.AuthenticateAsync("benchmark", "benchmark", CancellationToken.None).ConfigureAwait(false);
        return await ReadBatchesAsync(
            corpus.Articles.Select(article => new SegmentId(article.SegmentId)).ToArray(),
            scenario.BatchWidth,
            (ids, callback) => client.DecodedBodiesAsync(ids, callback, CancellationToken.None),
            counts,
            verifyHash).ConfigureAwait(false);
    }

    private static async Task<ReadResult> ReadProviderAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        bool verifyHash)
    {
        using var provider = CreateProvider(scenario, port);
        return await ReadBatchesAsync(
            corpus.Articles.Select(article => new SegmentId(article.SegmentId)).ToArray(),
            scenario.BatchWidth,
            (ids, callback) => provider.DecodedBodiesAsync(ids, callback, CancellationToken.None),
            counts,
            verifyHash).ConfigureAwait(false);
    }

    private static async Task<ReadResult> ReadBufferedStreamAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        CallbackCounts counts,
        InFlightArticleBudget budget,
        bool verifyHash,
        bool httpLike,
        int responseCopyChunkBytes,
        long copyStartedTimestamp)
    {
        using var provider = CreateProvider(scenario, port);
        var ids = corpus.Articles.Select(article => article.SegmentId).ToArray();
        var sizes = Enumerable.Repeat((long)scenario.DecodedArticleBytes, scenario.ArticleCount).ToArray();
        if (scenario.PrewarmConnections)
        {
            var articleWindow = scenario.ArticleBufferSize ?? Math.Max(scenario.BatchWidth * 2, 4);
            var remainingSegments = Math.Max(0, scenario.ArticleCount - 1);
            var plannedBatches = (remainingSegments + scenario.BatchWidth - 1) / scenario.BatchWidth;
            _ = provider.PrewarmConnectionsAsync(
                Math.Min(plannedBatches, articleWindow),
                CancellationToken.None);
        }
        await using var stream = MultiSegmentStream.Create(
            ids.AsMemory(),
            provider,
            articleBufferSize: scenario.ArticleBufferSize ?? Math.Max(scenario.BatchWidth * 2, 4),
            estimatedSegmentSize: scenario.DecodedArticleBytes,
            failFastOnFirstSegment: true,
            usePipelinedBodyRequests: true,
            cancellationToken: CancellationToken.None,
            fileName: "loopback.bin",
            exactSegmentSizes: sizes,
            inFlightArticleBudget: budget,
            bodyPipelineBatchWidth: scenario.BatchWidth);

        if (httpLike)
        {
            var sink = new HttpLikeCountingSink(responseCopyChunkBytes, copyStartedTimestamp);
            var sha256 = await sink.CopyFromAsync(stream, verifyHash, CancellationToken.None)
                .ConfigureAwait(false);
            return new ReadResult(sink.BytesWritten, sha256, sink.TimeToFirstByte);
        }
        if (verifyHash)
            return await CopyAndHashAsync(stream, CancellationToken.None).ConfigureAwait(false);

        await stream.CopyToAsync(Stream.Null, CancellationToken.None).ConfigureAwait(false);
        return new ReadResult(corpus.ExpectedBytes, null, null);
    }

    /// <summary>
    /// Streams the corpus through Native Cache read-through exactly as an uncached WebDAV GET
    /// does, then requires every byte to have been committed to the cache by the same read.
    /// </summary>
#pragma warning disable CA2000 // The store and queue move into AfterTiming (or the catch block) for disposal.
    private static async Task<ReadResult> ReadNativeCacheAsync(
        NntpWholePathScenario scenario,
        int port,
        NntpLoopbackCorpus corpus,
        InFlightArticleBudget budget,
        bool verifyHash,
        int responseCopyChunkBytes,
        long copyStartedTimestamp)
    {
        using var provider = CreateProvider(scenario, port);
        var directory = Directory.CreateTempSubdirectory("native-cold-");
        // Counts why blocks went uncached, for the failure message below.
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var metrics = new PrometheusMetrics(registry);
        var previousMetrics = PrometheusMetrics.Current;
        PrometheusMetrics.Current = metrics;
        NativeCacheStore? store = null;
        NativeCacheCommitQueue? queue = null;
        try
        {
            var ids = corpus.Articles.Select(article => article.SegmentId).ToArray();
            var size = (long)scenario.DecodedArticleBytes;
            var ranges = Enumerable.Range(0, ids.Length)
                .Select(index => new LongRange(index * size, (index + 1) * size)).ToArray();
            var identity = new NativeCacheIdentity("benchmark", "v1", corpus.ExpectedBytes);
            var writes = 0;
            store = new NativeCacheStore(Path.Join(directory.FullName, "index.db"),
                [new NativeCacheFolder { Path = directory.FullName, MinFreeBytes = 0 }])
            {
                BeforeWriteReserveAsync = scenario.CacheStallEveryNthWrite > 0 || scenario.CacheWriteDelayMs > 0
                    ? token =>
                    {
                        var delay = scenario.CacheWriteDelayMs;
                        if (scenario.CacheStallEveryNthWrite > 0
                            && Interlocked.Increment(ref writes) % scenario.CacheStallEveryNthWrite == 0)
                            delay += scenario.CacheStallMs;
                        return delay > 0 ? Task.Delay(delay, token) : Task.CompletedTask;
                    }
                    : null,
            };
            queue = scenario.CommitQueueCapacityBytes is { } capacity
                ? new NativeCacheCommitQueue(capacity)
                : new NativeCacheCommitQueue();
            var backfilled = 0L;
            if (scenario.ConcurrentReaders > 1)
            {
                // The store does not queue a brand-new entry's first commit behind another write
                // (writer_busy), so concurrent readers of an uncatalogued file would race to create
                // it. Catalogue the file's short tail block first, as for a file played before.
                var tail = (corpus.ExpectedBytes - 1) / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
                if (!await store.WriteBlockAsync(identity, tail, corpus.DecodedFile.AsMemory(checked((int)tail)))
                        .ConfigureAwait(false))
                    throw new InvalidOperationException("Could not catalogue the benchmark file.");
            }
            var slots = scenario.BufferSlots > 0 ? new NativeBufferSlots(scenario.BufferSlots) : null;
            var (cacheStore, commitQueue) = (store, queue);
            NativeCachedStream CreateStream() => new(cacheStore, identity,
                _ => Task.FromResult<Stream>(new NzbFileStream(ids, corpus.ExpectedBytes, provider,
                    scenario.ArticleBufferSize ?? Math.Max(scenario.BatchWidth * 2, 4), ranges,
                    usePipelinedBodyRequests: true, fileName: "loopback.bin", inFlightArticleBudget: budget,
                    streamingBodyBatchWidth: scenario.BatchWidth, segmentByteRangesTrusted: true)),
                () => true, slots, writeBehind: true, commitQueue: commitQueue,
                backfill: (_, length) => Interlocked.Add(ref backfilled, length));
            var streams = Enumerable.Range(0, Math.Max(1, scenario.ConcurrentReaders)).Select(_ => CreateStream()).ToArray();
            ReadResult result;
            try
            {
                if (streams.Length == 1)
                {
                    var sink = new HttpLikeCountingSink(responseCopyChunkBytes, copyStartedTimestamp);
                    var sha256 = await sink.CopyFromAsync(streams[0], verifyHash, CancellationToken.None).ConfigureAwait(false);
                    result = new ReadResult(sink.BytesWritten, sha256, sink.TimeToFirstByte);
                }
                else
                {
                    result = await ReadSlicesAsync(streams, corpus, verifyHash, responseCopyChunkBytes, copyStartedTimestamp,
                        scenario.ReaderBytesPerSecond, scenario.ReaderStartIntervalMs).ConfigureAwait(false);
                }
            }
            catch
            {
                foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            // The client has every byte here, as when an HTTP response finishes sending before its
            // handler disposes the stream. Disposal, trailing commits and verification follow untimed;
            // every byte must have been saved by this read, without any backfill.
            var (ownedStore, ownedQueue) = (store, queue);
            store = null;
            queue = null;
            return result with
            {
                AfterTiming = async () =>
                {
                    try
                    {
                        foreach (var stream in streams) await stream.DisposeAsync().ConfigureAwait(false);
                        await ownedQueue.DisposeAsync().ConfigureAwait(false);
                        var cached = await ownedStore.GetCoverageAsync(identity).ConfigureAwait(false);
                        if (cached != corpus.ExpectedBytes || backfilled != 0)
                            throw new InvalidOperationException(
                                $"Native cache committed {cached}/{corpus.ExpectedBytes} bytes during the uncached read " +
                                $"({backfilled} bytes deferred to backfill). Skips: {await SkipReasonsAsync(registry).ConfigureAwait(false)}");
                    }
                    finally
                    {
                        if (ReferenceEquals(PrometheusMetrics.Current, metrics)) PrometheusMetrics.Current = previousMetrics;
                        await ownedStore.DisposeAsync().ConfigureAwait(false);
                        directory.Delete(recursive: true);
                    }
                },
            };
        }
        catch
        {
            if (ReferenceEquals(PrometheusMetrics.Current, metrics)) PrometheusMetrics.Current = previousMetrics;
            if (queue is not null) await queue.DisposeAsync().ConfigureAwait(false);
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            directory.Delete(recursive: true);
            throw;
        }
    }
#pragma warning restore CA2000

    private static async Task<string> SkipReasonsAsync(Prometheus.CollectorRegistry registry)
    {
        using var output = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(output).ConfigureAwait(false);
        return string.Join(", ", System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n')
            .Where(line => line.StartsWith("nzbdav_native_cache_skipped_total{", StringComparison.Ordinal) || line.Contains("buffer_admission\"} ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Concurrent players: each stream reads its own block-aligned slice of the file as a ranged
    /// response at a steady playback rate, so readers share buffer slots and connections but never
    /// fill the same block. A reader that falls well behind its rate fails the run as stalled.
    /// </summary>
    private static readonly TimeSpan MaxPlayerStall = TimeSpan.FromSeconds(3);

    private static async Task<ReadResult> ReadSlicesAsync(
        NativeCachedStream[] streams,
        NntpLoopbackCorpus corpus,
        bool verifyHash,
        int responseCopyChunkBytes,
        long copyStartedTimestamp,
        long paceBytesPerSecond,
        int startIntervalMs)
    {
        var blocksPerReader = corpus.ExpectedBytes / NativeCacheStore.BlockSize / streams.Length;
        if (blocksPerReader == 0) throw new InvalidOperationException("The corpus is too small for this many readers.");
        var slices = await Task.WhenAll(streams.Select((stream, index) => Task.Run(async () =>
        {
            var start = index * blocksPerReader * NativeCacheStore.BlockSize;
            var end = index == streams.Length - 1 ? corpus.ExpectedBytes : start + blocksPerReader * NativeCacheStore.BlockSize;
            await Task.Delay(index * startIntervalMs).ConfigureAwait(false);
            RangeContext.SetReadBudget(end - start);
            stream.Position = start;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(responseCopyChunkBytes);
            try
            {
                TimeSpan? firstByte = null;
                var playing = Stopwatch.StartNew();
                var matches = true;
                var position = start;
                while (position < end)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(responseCopyChunkBytes, end - position)))
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    if (firstByte is null)
                    {
                        firstByte = Stopwatch.GetElapsedTime(copyStartedTimestamp);
                        playing.Restart();
                    }
                    if (verifyHash)
                        matches &= buffer.AsSpan(0, read).SequenceEqual(corpus.DecodedFile.AsSpan(checked((int)position), read));
                    position += read;
                    var ahead = TimeSpan.FromSeconds((double)(position - start) / paceBytesPerSecond) - playing.Elapsed;
                    if (ahead > TimeSpan.Zero) await Task.Delay(ahead).ConfigureAwait(false);
                }
                // Playback time beyond the slice's duration at the target rate is time the player stalled.
                var stalled = playing.Elapsed - TimeSpan.FromSeconds((double)(position - start) / paceBytesPerSecond);
                return (Bytes: position - start, Matches: matches, FirstByte: firstByte, Stalled: stalled);
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        }))).ConfigureAwait(false);
        var worst = slices.Max(slice => slice.Stalled);
        if (worst > MaxPlayerStall)
            throw new InvalidOperationException($"A player stalled for {worst.TotalSeconds:F1} s behind its playback rate.");
        var total = slices.Sum(slice => slice.Bytes);
        // Every slice compared byte for byte against the corpus stands in for the whole-file hash.
        var sha256 = verifyHash
            ? slices.All(slice => slice.Matches) && total == corpus.ExpectedBytes ? corpus.ExpectedSha256 : "mismatch"
            : null;
        return new ReadResult(total, sha256, slices.Min(slice => slice.FirstByte));
    }

#pragma warning disable CA2000 // MultiProviderNntpClient owns and disposes its MultiConnectionNntpClient and pool.
    private static MultiProviderNntpClient CreateProvider(NntpWholePathScenario scenario, int port)
    {
        var pool = new ConnectionPool<INntpClient>(
            scenario.ConnectionCount,
            async cancellationToken =>
            {
                var client = new BaseNntpClient(new UsenetClient(CreateOptions(scenario)));
                await client.ConnectAsync("127.0.0.1", port, useSsl: false, cancellationToken).ConfigureAwait(false);
                await client.AuthenticateAsync("benchmark", "benchmark", cancellationToken).ConfigureAwait(false);
                return client;
            },
            diagnosticName: "loopback");
        var connection = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("loopback"),
            "loopback",
            pipeliningDepth: scenario.BatchWidth,
            maxTransferConnections: scenario.ConnectionCount);
        return new MultiProviderNntpClient([connection]);
    }
#pragma warning restore CA2000

    private static UsenetClientOptions CreateOptions(NntpWholePathScenario scenario) => new()
    {
        CrcValidation = scenario.CrcValidation,
        ReadTimeout = TimeSpan.FromSeconds(30),
        MaxPipelineDepth = Math.Max(scenario.BatchWidth, 8),
    };

    private static async Task<ReadResult> ReadBatchesAsync(
        SegmentId[] ids,
        int width,
        Func<IReadOnlyList<SegmentId>, ArticleBodyCompletionHandler, Task<UsenetDecodedBodyBatch>> readBatch,
        CallbackCounts counts,
        bool verifyHash)
    {
        using var hash = verifyHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        long total = 0;
        for (var start = 0; start < ids.Length; start += width)
        {
            var batchIds = new SegmentId[Math.Min(width, ids.Length - start)];
            Array.Copy(ids, start, batchIds, 0, batchIds.Length);
            var batch = await readBatch(batchIds, counts.Record).ConfigureAwait(false);
            foreach (var responseTask in batch.Responses)
            {
                var response = await responseTask.ConfigureAwait(false);
                if (!response.Success || response.Stream is null)
                    throw new InvalidOperationException($"Loopback BODY failed with {response.ResponseCode}.");
                await using var stream = response.Stream;
                total += await DrainAsync(stream, hash, CancellationToken.None).ConfigureAwait(false);
            }
            await batch.Completion.ConfigureAwait(false);
        }
        return new ReadResult(
            total,
            hash is null ? null : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            null);
    }

    private static async Task<ReadResult> CopyAndHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        return new ReadResult(
            total,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            null);
    }

    private static async Task<int> DrainAsync(Stream stream, IncrementalHash? hash, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            hash?.AppendData(buffer, 0, read);
            total += read;
        }
    }

    private static NntpWholePathTiming Add(NntpWholePathTiming left, NntpWholePathTiming right) => new(
        left.WallSeconds + right.WallSeconds,
        left.ClientCpuSeconds + right.ClientCpuSeconds,
        left.ServerCpuSeconds + right.ServerCpuSeconds,
        left.ClientCpuSecondsPerGb + right.ClientCpuSecondsPerGb,
        left.ThroughputMbps + right.ThroughputMbps,
        left.FirstByteMs + right.FirstByteMs,
        left.TimeToPeakActiveMs + right.TimeToPeakActiveMs,
        left.ClientAllocatedBytes + right.ClientAllocatedBytes,
        left.Gen0Collections + right.Gen0Collections,
        left.Gen1Collections + right.Gen1Collections,
        left.Gen2Collections + right.Gen2Collections);

    private static NntpWholePathTiming Divide(NntpWholePathTiming value, int divisor) => new(
        value.WallSeconds / divisor,
        value.ClientCpuSeconds / divisor,
        value.ServerCpuSeconds / divisor,
        value.ClientCpuSecondsPerGb / divisor,
        value.ThroughputMbps / divisor,
        value.FirstByteMs / divisor,
        value.TimeToPeakActiveMs / divisor,
        value.ClientAllocatedBytes / divisor,
        value.Gen0Collections / divisor,
        value.Gen1Collections / divisor,
        value.Gen2Collections / divisor);

    /// <param name="AfterTiming">Verification and cleanup that must not count as client time.</param>
    private readonly record struct ReadResult(long Count, string? Sha256, TimeSpan? TimeToFirstByte, Func<Task>? AfterTiming = null);

    private sealed class CallbackCounts
    {
        private long _retrieved;
        private long _cancelled;
        private long _notFound;
        private long _notRetrieved;

        public long Retrieved => Interlocked.Read(ref _retrieved);
        public long Cancelled => Interlocked.Read(ref _cancelled);
        public long NotFound => Interlocked.Read(ref _notFound);
        public long NotRetrieved => Interlocked.Read(ref _notRetrieved);

        public void Record(ArticleBodyResult result, string? failureReason)
        {
            _ = failureReason;
            switch (result)
            {
                case ArticleBodyResult.Retrieved:
                    Interlocked.Increment(ref _retrieved);
                    break;
                case ArticleBodyResult.Cancelled:
                    Interlocked.Increment(ref _cancelled);
                    break;
                case ArticleBodyResult.NotFound:
                    Interlocked.Increment(ref _notFound);
                    break;
                case ArticleBodyResult.NotRetrieved:
                    Interlocked.Increment(ref _notRetrieved);
                    break;
            }
        }
    }
}

internal sealed record LoopbackServerArguments(
    int ArticleCount,
    int DecodedArticleBytes,
    int Seed,
    int RoundTripDelayMs,
    int HandshakeDelayMs,
    long? BandwidthBytesPerSecond,
    string CountersPath,
    IReadOnlyList<string> MissingIds);

internal sealed class LoopbackServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _countersPath;
    private readonly TimeSpan _cpuBefore;

    private LoopbackServerProcess(Process process, string countersPath)
    {
        _process = process;
        _countersPath = countersPath;
        _cpuBefore = process.TotalProcessorTime;
    }

    public int Port { get; private init; }
    public double ProcessCpuSeconds { get; private set; }

    public static async Task<LoopbackServerProcess> StartAsync(
        NntpWholePathScenario scenario,
        string countersPath)
    {
        var processPath = Environment.ProcessPath ?? "dotnet";
        var startInfo = new ProcessStartInfo(processPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(typeof(NntpWholePathReport).Assembly.Location);
        startInfo.ArgumentList.Add("--nntp-loopback-server");
        startInfo.ArgumentList.Add("--articles");
        startInfo.ArgumentList.Add(scenario.ArticleCount.ToString());
        startInfo.ArgumentList.Add("--article-bytes");
        startInfo.ArgumentList.Add(scenario.DecodedArticleBytes.ToString());
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(NntpWholePathReport.CorpusSeed.ToString());
        startInfo.ArgumentList.Add("--rtt-ms");
        startInfo.ArgumentList.Add(scenario.RoundTripDelayMs.ToString());
        startInfo.ArgumentList.Add("--handshake-ms");
        startInfo.ArgumentList.Add(scenario.HandshakeDelayMs.ToString());
        if (scenario.BandwidthBytesPerSecond is { } bandwidth)
        {
            startInfo.ArgumentList.Add("--bandwidth-bps");
            startInfo.ArgumentList.Add(bandwidth.ToString());
        }
        startInfo.ArgumentList.Add("--counters-out");
        startInfo.ArgumentList.Add(countersPath);

        var process = Process.Start(startInfo) ??
                      throw new InvalidOperationException("Could not start loopback NNTP server process.");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal) ||
                !int.TryParse(line[6..], out var port))
            {
                throw new InvalidOperationException("Loopback NNTP server did not report a valid READY port.");
            }
            return new LoopbackServerProcess(process, countersPath) { Port = port };
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.Dispose();
            throw new InvalidOperationException(
                $"Loopback NNTP server did not become ready: {error}");
        }
    }

    public async Task<NntpLoopbackServerSnapshot> StopAndGetSnapshotAsync()
    {
        var cpuAfter = _cpuBefore;
        if (!_process.HasExited)
        {
            _process.Refresh();
            cpuAfter = _process.TotalProcessorTime;
        }
        if (!_process.HasExited)
        {
            await _process.StandardInput.WriteLineAsync("STOP").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        ProcessCpuSeconds = Math.Max(0, (cpuAfter - _cpuBefore).TotalSeconds);
        if (_process.ExitCode != 0)
        {
            var error = await _process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Loopback NNTP server exited with {_process.ExitCode}: {error}");
        }
        var json = await File.ReadAllTextAsync(_countersPath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NntpLoopbackServerSnapshot>(json) ??
               throw new InvalidDataException("Loopback server did not write counters.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        _process.Dispose();
    }
}
