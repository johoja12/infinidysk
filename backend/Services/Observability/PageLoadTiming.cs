using System.Diagnostics;
using Serilog;

namespace NzbWebDAV.Services.Observability;

/// <summary>Stage timings without media paths, user identities, or credential-bearing URLs.</summary>
internal sealed class PageLoadTiming(string stage) : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (elapsed >= 1000)
            Log.Information("Slow media page stage {Stage}: {ElapsedMs:0}ms", stage, elapsed);
        else
            Log.Debug("Media page stage {Stage}: {ElapsedMs:0}ms", stage, elapsed);
    }
}
