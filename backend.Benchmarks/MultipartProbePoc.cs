using System.Diagnostics;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using NzbWebDAV.WebDav.Requests;
using UsenetSharp.Clients;
using UsenetSharp.Models;

namespace NzbWebDAV.Benchmarks;

/// <summary>Opt-in timing experiment through real NNTP pools, yEnc validation and multipart reads.</summary>
internal static class MultipartProbePoc
{
    private const int ReadBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };
    private sealed record Profile(string Name, int Connections, int Prewarm, int HandshakeMs, bool CachedHeaders = false);
    private sealed record Sample(string Profile, double FirstByteMs, double FirstMiBMs, double CompleteMs,
        double ProbeWallMs, int MaximumProbes, long BodyCommands, bool BytesMatch, bool Verified);

    public static async Task RunAsync(string[] args)
    {
        var outputPath = args.Length > 1 ? args[1] : "multipart-probe-poc.json";
        var repetitions = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 5;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(repetitions);
        var corpus = NntpLoopbackCorpus.Create(8, 768 * 1024, 823);
        Profile[] profiles =
        [
            new("two-warm-connections", 2, 2, 0),
            new("one-connection-limit", 1, 1, 0),
            new("one-warm-slow-second", 2, 1, 1_500),
            new("cached-headers", 2, 2, 0, true),
            new("cold-connections", 2, 0, 150),
        ];
        // JIT/native initialization is outside the recorded samples.
        await MeasureAsync(new Profile("jit", 2, 2, 0), corpus).ConfigureAwait(false);
        var samples = new List<Sample>();
        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            // Alternate profile order to expose drift rather than always favoring one end.
            foreach (var profile in repetition % 2 == 0 ? profiles : profiles.Reverse())
            {
                var sample = await MeasureAsync(profile, corpus).ConfigureAwait(false);
                samples.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
            }
        }
        var report = new
        {
            Method = "Fresh loopback provider pools and header caches per sample; 450 ms BODY response delay, 6 MB/s per connection, real CRC-validated generated articles. Prewarm is outside the measurement. This is a controlled experiment, not a live-provider claim.",
            Samples = samples,
            Summary = profiles.Select(p =>
            {
                var rows = samples.Where(s => s.Profile == p.Name).ToArray();
                return new { p.Name, Samples = rows.Length,
                    MedianFirstByteMs = Median(rows.Select(s => s.FirstByteMs)),
                    MedianFirstMiBMs = Median(rows.Select(s => s.FirstMiBMs)),
                    MedianCompleteMs = Median(rows.Select(s => s.CompleteMs)),
                    MedianProbeWallMs = Median(rows.Select(s => s.ProbeWallMs)),
                    MaximumProbes = rows.Max(s => s.MaximumProbes),
                    MinimumBodyCommands = rows.Min(s => s.BodyCommands), MaximumBodyCommands = rows.Max(s => s.BodyCommands),
                    AllBytesMatch = rows.All(s => s.BytesMatch), AllVerified = rows.All(s => s.Verified) };
            }).ToArray(),
        };
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, ReportJsonOptions)).ConfigureAwait(false);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return Math.Round((sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2, 2);
    }

    private static async Task<Sample> MeasureAsync(Profile profile, NntpLoopbackCorpus corpus)
    {
        var scenario = new NntpWholePathScenario(profile.Name, NntpWholePathLayer.BufferedStream, false,
            corpus.Articles.Count, 768 * 1024, profile.Connections, 4, 450, 6_000_000, YencCrcValidationMode.Require);
        await using var server = await NntpLoopbackServer.StartAsync(corpus, scenario.RoundTripDelayMs,
            scenario.BandwidthBytesPerSecond, profile.HandshakeMs).ConfigureAwait(false);
        using var provider = NntpWholePathReport.CreateProvider(scenario, server.Port);
        if (profile.Prewarm > 0)
            await provider.PrewarmConnectionsAsync(profile.Prewarm, CancellationToken.None).ConfigureAwait(false);
        using var headers = new HeaderCachingNntpClient(provider);
        using var client = new ProbeTimingClient(headers);
        if (profile.CachedHeaders)
        {
            foreach (var index in new[] { 0, 1, corpus.Articles.Count - 1 })
                await client.GetYencHeadersAsync(corpus.Articles[index].SegmentId, CancellationToken.None).ConfigureAwait(false);
            client.Reset();
        }
        var multipart = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                FileParts = [new DavMultipartFile.FilePart
                {
                    SegmentIds = corpus.Articles.Select(a => a.SegmentId).ToArray(),
                    SegmentIdByteRange = new LongRange(0, corpus.ExpectedBytes),
                    FilePartByteRange = new LongRange(0, corpus.ExpectedBytes),
                }],
            },
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var priority = cts.Token.SetContext(new DownloadPriorityContext { Priority = SemaphorePriority.High });
        using var native = new NativeCacheReadContext(ReadBytes);
        RangeContext.SetReadBudget(ReadBytes);
        try
        {
            await using var stream = new DavMultipartFileStream(multipart, client, 40, resolver: null,
                usePipelinedBodyRequests: true, fileName: "multipart-probe.bin");
            var output = new byte[ReadBytes];
            var clock = Stopwatch.StartNew();
            await stream.ReadExactlyAsync(output.AsMemory(0, 1), cts.Token).ConfigureAwait(false);
            var firstByte = clock.Elapsed.TotalMilliseconds;
            await stream.ReadExactlyAsync(output.AsMemory(1, 1024 * 1024 - 1), cts.Token).ConfigureAwait(false);
            var firstMiB = clock.Elapsed.TotalMilliseconds;
            await stream.ReadExactlyAsync(output.AsMemory(1024 * 1024), cts.Token).ConfigureAwait(false);
            var complete = clock.Elapsed.TotalMilliseconds;
            var matches = output.AsSpan().SequenceEqual(corpus.DecodedFile.AsSpan(0, output.Length));
            if (!matches || !stream.LastReadCacheable) throw new InvalidDataException("Multipart PoC returned unverified or mismatched bytes.");
            return new Sample(profile.Name, firstByte, firstMiB, complete, client.WallMs,
                client.Maximum, server.GetSnapshot().BodyCommands, matches, stream.LastReadCacheable);
        }
        finally { RangeContext.SetReadBudget(null); }
    }

    private sealed class ProbeTimingClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        private int _active;
        private long _first;
        private long _last;
        public int Maximum { get; private set; }
        public double WallMs => _first == 0 ? 0 : Stopwatch.GetElapsedTime(_first, _last).TotalMilliseconds;
        public void Reset() { _first = 0; _last = 0; Maximum = 0; }
        public override async Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _active);
            Maximum = Math.Max(Maximum, active);
            Interlocked.CompareExchange(ref _first, Stopwatch.GetTimestamp(), 0);
            try { return await base.GetYencHeadersAsync(segmentId, ct).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp()); Interlocked.Decrement(ref _active); }
        }
    }
}
