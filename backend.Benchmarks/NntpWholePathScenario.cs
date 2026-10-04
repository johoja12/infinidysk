using UsenetSharp.Clients;

namespace NzbWebDAV.Benchmarks;

internal enum NntpWholePathLayer
{
    Transport,
    Provider,
    BufferedStream,
    HttpLike,
    NativeCache,
}

internal sealed record NntpWholePathScenario(
    string Name,
    NntpWholePathLayer Layer,
    bool UseTls,
    int ArticleCount,
    int DecodedArticleBytes,
    int ConnectionCount,
    int BatchWidth,
    int RoundTripDelayMs,
    long? BandwidthBytesPerSecond,
    YencCrcValidationMode CrcValidation)
{
    public int HandshakeDelayMs { get; init; }
    public int? ArticleBufferSize { get; init; }
    public bool PrewarmConnections { get; init; }
    /// <summary>Native cache layer only: stall every Nth cache block write (a busy NAS).</summary>
    public int CacheStallEveryNthWrite { get; init; }
    public int CacheStallMs { get; init; }
    /// <summary>Native cache layer only: concurrent readers, each streaming its own block-aligned slice.</summary>
    public int ConcurrentReaders { get; init; } = 1;
    /// <summary>Native cache layer only: shared 4 MiB buffer slots (0 = unbounded).</summary>
    public int BufferSlots { get; init; }
    /// <summary>Native cache layer with concurrent readers: each reader's steady playback rate.</summary>
    public long ReaderBytesPerSecond { get; init; } = 3_000_000;
    /// <summary>Native cache layer with concurrent readers: delay between successive players starting.</summary>
    public int ReaderStartIntervalMs { get; init; }
    /// <summary>Native cache layer only: fixed latency added to every cache block write (a slow NAS commit).</summary>
    public int CacheWriteDelayMs { get; init; }
    /// <summary>Native cache layer only: commit queue byte budget; null uses the production default.</summary>
    public long? CommitQueueCapacityBytes { get; init; }
    // Awaits prewarming before the measurement origin instead of racing it at read start.
    public bool WarmStart { get; init; }

    public static IReadOnlyList<NntpWholePathScenario> Quick =>
    [
        new("plain-transport-w4", NntpWholePathLayer.Transport, false, 8, 256 * 1024, 1, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-provider-w4", NntpWholePathLayer.Provider, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w1", NntpWholePathLayer.BufferedStream, false, 8, 256 * 1024, 4, 1, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w4", NntpWholePathLayer.BufferedStream, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-http-like-w4", NntpWholePathLayer.HttpLike, false, 8, 256 * 1024, 4, 4, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> Sustained =>
    [
        new("plain-buffered-w1", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 1, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w2", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 2, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w4", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 4, 0, null, YencCrcValidationMode.Require),
        new("plain-buffered-w8", NntpWholePathLayer.BufferedStream, false, 256, 4 * 1024 * 1024, 20, 8, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> Profile =>
    [
        new("plain-http-like-w4", NntpWholePathLayer.HttpLike, false, 64, 4 * 1024 * 1024, 20, 4, 0, null, YencCrcValidationMode.Require),
    ];

    public static IReadOnlyList<NntpWholePathScenario> Cold =>
    [
        new("cold-ramp-256mib-w4", NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, 20, 4, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 150,
            ArticleBufferSize = 40,
        },
        new("cold-ramp-256mib-w4-prewarm", NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, 20, 4, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 150,
            ArticleBufferSize = 40,
            PrewarmConnections = true,
        },
    ];

    /// <summary>
    /// Uncached playback while Native Cache commits every byte: NativeCachedStream over
    /// NzbFileStream, as a WebDAV GET sees it. Each response waits 250 ms and connections
    /// are capped at 4 MB/s (~1.7 MB/s per connection for 768 KiB articles, like distant
    /// providers), so throughput tracks how many connections the stream keeps busy.
    /// </summary>
    public static IReadOnlyList<NntpWholePathScenario> NativeCold =>
    [
        new("native-cold-256mib-rtt250-w4", NntpWholePathLayer.NativeCache, false, 342, 768 * 1024, 40, 4, 250, 4_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 250,
            ArticleBufferSize = 40,
        },
        // The same playback while every 10th cache block write stalls for 1.5 s, longer than the
        // response path may wait. Playback must not slow down and every byte must still be cached.
        new("native-cold-256mib-rtt250-w4-nas-stall", NntpWholePathLayer.NativeCache, false, 342, 768 * 1024, 40, 4, 250, 4_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 250,
            ArticleBufferSize = 40,
            CacheStallEveryNthWrite = 10,
            CacheStallMs = 1500,
        },
        // Eight players at 3 MB/s each (a high-bitrate film), started 1.5 s apart, streaming
        // different parts of the file through four buffer slots (a 16 MiB budget). Slots are held only while a block fills, so
        // no player stalls and every byte is cached by the same reads; per-response admission would
        // leave half the players uncached.
        new("native-cold-256mib-rtt250-w4-8players-4slots", NntpWholePathLayer.NativeCache, false, 342, 768 * 1024, 40, 4, 250, 4_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 250,
            ArticleBufferSize = 40,
            ConcurrentReaders = 8,
            BufferSlots = 4,
            ReaderBytesPerSecond = 3_000_000,
            ReaderStartIntervalMs = 1500,
        },
        // A fast stream (~40 MB/s: 20 connections at 4 MB/s, 50 ms RTT) over a NAS where every 4 MiB block commit takes 250 ms, as
        // measured on Synology NFS. Serial commits of one file manage ~16 MB/s, so a 64 MiB queue
        // (a 256 MiB queue against a multi-GB file) overflows into backfill; blocks of the same
        // file must commit in parallel for every byte to be cached by the read itself.
        new("native-cold-256mib-fast-commit250", NntpWholePathLayer.NativeCache, false, 342, 768 * 1024, 20, 4, 50, 4_000_000, YencCrcValidationMode.Require)
        {
            HandshakeDelayMs = 50,
            ArticleBufferSize = 40,
            CacheWriteDelayMs = 250,
            CommitQueueCapacityBytes = 64L * 1024 * 1024,
        },
    ];

    // Warm, paced connections isolate ordered-delivery stalls from connection-ramp latency.
    public static IReadOnlyList<NntpWholePathScenario> Smoothness =>
    [
        Paced("paced-256mib-w1", batchWidth: 1),
        Paced("paced-256mib-w4", batchWidth: 4),
        Paced("paced-256mib-w8", batchWidth: 8),
        // Fewer connections than the default window can use: scheduling must not
        // depend on spare capacity to stay steady.
        Paced("paced-256mib-w4-4conn", batchWidth: 4, connections: 4),
        // One stripe per buffered article: a full-batch-per-stripe start would fill the whole window.
        Paced("paced-256mib-w4-40conn", batchWidth: 4, connections: 40),
    ];

    private static NntpWholePathScenario Paced(string name, int batchWidth, int connections = 20) =>
        new(name, NntpWholePathLayer.HttpLike, false, 342, 768 * 1024, connections, batchWidth, 40, 6_000_000, YencCrcValidationMode.Require)
        {
            ArticleBufferSize = 40,
            WarmStart = true,
        };

    public static IReadOnlyList<NntpWholePathScenario> ForSet(string set) =>
        set.Equals("quick", StringComparison.OrdinalIgnoreCase)
            ? Quick
            : set.Equals("sustained", StringComparison.OrdinalIgnoreCase)
                ? Sustained
                : set.Equals("profile", StringComparison.OrdinalIgnoreCase)
                    ? Profile
                    : set.Equals("cold", StringComparison.OrdinalIgnoreCase)
                        ? Cold
                        : set.Equals("native-cold", StringComparison.OrdinalIgnoreCase)
                            ? NativeCold
                            : throw new ArgumentException(
                                "--set must be 'quick', 'sustained', 'profile', 'cold', or 'native-cold'.",
                        : set.Equals("smoothness", StringComparison.OrdinalIgnoreCase)
                            ? Smoothness
                            : throw new ArgumentException(
                                "--set must be 'quick', 'sustained', 'profile', 'cold', or 'smoothness'.",
                                nameof(set));
}
