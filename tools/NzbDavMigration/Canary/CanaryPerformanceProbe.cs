using System.Diagnostics;

namespace NzbDavMigration.Canary;

internal interface ICanaryMonotonicClock
{
    long GetTimestamp();
    TimeSpan GetElapsed(long start, long end);
}

internal sealed class StopwatchCanaryClock : ICanaryMonotonicClock
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsed(long start, long end) => Stopwatch.GetElapsedTime(start, end);
}

public sealed class CanaryPerformanceProbe
{
    private const int SeekReadBytes = 8 * 1024 * 1024;
    private const int SequentialReadBytes = 128 * 1024 * 1024;
    private readonly ICanaryMonotonicClock _clock;

    public CanaryPerformanceProbe() : this(new StopwatchCanaryClock())
    {
    }

    internal CanaryPerformanceProbe(ICanaryMonotonicClock clock)
    {
        _clock = clock;
    }

    public async Task<IReadOnlyList<CanaryPerformanceObservation>> ProbeAsync(
        CanaryBenchmarkFile file,
        string side,
        string pass,
        CanaryRouteDescription route,
        Func<string, Stream> openStream,
        TimeSpan timeout,
        string? provenCacheLabel = null,
        CancellationToken cancellationToken = default)
    {
        route.Validate();
        if (side is not ("legacy" or "infinidysk"))
            throw new ArgumentOutOfRangeException(nameof(side));
        if (pass is not ("first-pass" or "repeat-pass"))
            throw new ArgumentOutOfRangeException(nameof(pass));
        var operations = new[]
        {
            (Name: "seek-10", Offset: file.ExpectedFileSize / 10,
                Requested: Math.Min((long)SeekReadBytes, file.ExpectedFileSize - file.ExpectedFileSize / 10)),
            (Name: "seek-50", Offset: file.ExpectedFileSize / 2,
                Requested: Math.Min((long)SeekReadBytes, file.ExpectedFileSize - file.ExpectedFileSize / 2)),
            (Name: "seek-90", Offset: file.ExpectedFileSize * 9 / 10,
                Requested: Math.Min((long)SeekReadBytes, file.ExpectedFileSize - file.ExpectedFileSize * 9 / 10)),
            (Name: "sequential", Offset: 0L,
                Requested: Math.Min((long)SequentialReadBytes, file.ExpectedFileSize)),
        };
        var results = new List<CanaryPerformanceObservation>(operations.Length);
        foreach (var operation in operations)
        {
            long actual = 0;
            long? firstByte = null;
            var start = _clock.GetTimestamp();
            var timedOut = false;
            string? error = null;
            try
            {
                await using var stream = openStream(file.LibraryRelativePath);
                if (!stream.CanRead || !stream.CanSeek)
                    throw new InvalidDataException("Benchmark stream must be readable and seekable.");
                stream.Seek(operation.Offset, SeekOrigin.Begin);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(timeout);
                var buffer = new byte[1024 * 1024];
                while (actual < operation.Requested)
                {
                    var request = (int)Math.Min(buffer.Length, operation.Requested - actual);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, request), bounded.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;
                    actual += read;
                    firstByte ??= _clock.GetTimestamp();
                }
                if (actual != operation.Requested)
                    error = $"Short read: requested {operation.Requested} bytes, received {actual}.";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                error = $"Timed out after {timeout}.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception.Message;
            }
            var end = _clock.GetTimestamp();
            var elapsed = _clock.GetElapsed(start, end);
            double? ttfb = firstByte is null ? null : _clock.GetElapsed(start, firstByte.Value).TotalMilliseconds;
            double? throughput = actual > 0 && elapsed.TotalSeconds > 0
                ? actual / 1024d / 1024d / elapsed.TotalSeconds
                : null;
            var cache = provenCacheLabel ?? (pass == "first-pass"
                ? "cache-state-unknown-first-pass"
                : "cache-state-unknown-repeat-pass");
            results.Add(new CanaryPerformanceObservation(
                file.LibraryRelativePath, side, pass, operation.Name, operation.Offset,
                operation.Requested, actual, ttfb, elapsed.TotalMilliseconds, throughput,
                cache, route, side == "infinidysk" && route.RouteKind == "frontend-proxied",
                timedOut, error));
        }
        return results;
    }
}
