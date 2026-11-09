// Only application telemetry and navigation are replaced. Cache storage, identity,
// repair ledger, filesystem anchoring and MemoryPack models compile from 1.x unchanged.
namespace NzbWebDAV.Database.Models { public class DavItem { } }
namespace NzbWebDAV.Services.Observability {
    public class PrometheusMetrics {
        public static PrometheusMetrics? Current => null;
        public void RecordNativeCacheSkip(string reason) { }
        public void RecordNativeCachePhase(string phase, TimeSpan duration) { }
    }
}
namespace Serilog { public static class Log { public static void Warning(string message, params object[] args) { } } }
namespace NzbWebDAV.Services.NativeCache {
    public sealed record NativeCacheTrafficBucket(long Bucket, long HitBlocks, long HitBytes, long MissBlocks, long MissBytes, long CommittedBytes);
}
