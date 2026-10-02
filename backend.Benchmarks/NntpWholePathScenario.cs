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
    ];

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
                                nameof(set));
}
