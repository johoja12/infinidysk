using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Process-wide memory of yEnc articles a provider answered with data from a different
/// post (its yEnc total contradicts the file's segment count). Such an answer is
/// deterministic for one file: asking the same provider again only re-downloads the
/// foreign body and discards the connection. The tracker lets the provider walk skip
/// those providers and lets a stream stop once a file has proven unservable, instead
/// of re-walking every provider for every retried range.
/// </summary>
internal static class MismatchedArticleTracker
{
    /// <summary>Distinct foreign articles, with none matching, that prove the file unservable.</summary>
    internal const int ForeignArticlesForFileVerdict = 4;

    /// <summary>
    /// Evidence expires so a repaired or re-posted release, or a provider that has since
    /// replaced its copy, is asked again.
    /// </summary>
    internal static readonly TimeSpan EvidenceLifetime = TimeSpan.FromMinutes(10);

    private const int MaxTrackedFiles = 4096;
    private const int MaxTrackedArticlesPerFile = 4096;

    private static readonly ConcurrentDictionary<string, FileState> Files = new(StringComparer.Ordinal);
    private static int _callCount;

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    internal static void ResetForTests()
    {
        Files.Clear();
        Clock = TimeProvider.System;
        Volatile.Write(ref _callCount, 0);
    }

    /// <summary>
    /// Records a foreign answer. Returns the file's distinct foreign-article count and
    /// whether this answer first established the file verdict.
    /// </summary>
    public static (int ForeignArticles, int Rejections, bool VerdictReached) RecordForeign(
        string fileKey, string articleKey, string providerKey)
    {
        var now = Clock.GetUtcNow();
        var state = GetState(fileKey, now);
        lock (state)
        {
            ExpireIfStale(state, now);
            state.LastEventUtc = now;
            state.Rejections++;
            if (!state.ForeignProviders.TryGetValue(articleKey, out var providers)
                && state.ForeignProviders.Count < MaxTrackedArticlesPerFile)
            {
                providers = new HashSet<string>(StringComparer.Ordinal);
                state.ForeignProviders[articleKey] = providers;
            }

            providers?.Add(providerKey);
            var reached = !state.VerdictReached && HasVerdict(state);
            if (reached) state.VerdictReached = true;
            return (state.ForeignProviders.Count, state.Rejections, reached);
        }
    }

    /// <summary>Records that a provider returned the expected post for one of the file's articles.</summary>
    public static void RecordMatch(string fileKey)
    {
        var now = Clock.GetUtcNow();
        var state = GetState(fileKey, now);
        lock (state)
        {
            ExpireIfStale(state, now);
            state.LastEventUtc = now;
            state.MatchedArticles++;
            state.VerdictReached = false;
        }
    }

    public static bool IsKnownForeign(string fileKey, string articleKey, string providerKey)
    {
        if (!Files.TryGetValue(fileKey, out var state)) return false;
        var now = Clock.GetUtcNow();
        lock (state)
        {
            ExpireIfStale(state, now);
            return state.ForeignProviders.TryGetValue(articleKey, out var providers)
                && providers.Contains(providerKey);
        }
    }

    /// <summary>
    /// True once enough distinct articles of the file came back foreign, and none came back
    /// matching, that further reads can only gap-fill or fail.
    /// </summary>
    public static bool TryGetFileVerdict(string fileKey, out int foreignArticles)
    {
        foreignArticles = 0;
        if (!Files.TryGetValue(fileKey, out var state)) return false;
        var now = Clock.GetUtcNow();
        lock (state)
        {
            ExpireIfStale(state, now);
            foreignArticles = state.ForeignProviders.Count;
            return HasVerdict(state);
        }
    }

    private static bool HasVerdict(FileState state) =>
        state.MatchedArticles == 0 && state.ForeignProviders.Count >= ForeignArticlesForFileVerdict;

    private static FileState GetState(string fileKey, DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _callCount) % 256 == 0 || Files.Count >= MaxTrackedFiles)
            Cleanup(now);
        return Files.GetOrAdd(fileKey, static _ => new FileState());
    }

    // Caller must hold the state lock.
    private static void ExpireIfStale(FileState state, DateTimeOffset now)
    {
        if (state.LastEventUtc == default || now - state.LastEventUtc < EvidenceLifetime) return;
        state.ForeignProviders.Clear();
        state.MatchedArticles = 0;
        state.Rejections = 0;
        state.VerdictReached = false;
    }

    private static void Cleanup(DateTimeOffset now)
    {
        foreach (var entry in Files)
        {
            lock (entry.Value)
            {
                if (now - entry.Value.LastEventUtc >= EvidenceLifetime)
                    Files.TryRemove(entry.Key, out _);
            }
        }

        // Still over the bound after expiry: drop everything rather than grow unbounded.
        // Losing the evidence only costs one more provider walk per article.
        if (Files.Count >= MaxTrackedFiles)
            Files.Clear();
    }

    private sealed class FileState
    {
        public DateTimeOffset LastEventUtc { get; set; }
        public Dictionary<string, HashSet<string>> ForeignProviders { get; } = new(StringComparer.Ordinal);
        public int MatchedArticles { get; set; }
        public int Rejections { get; set; }
        public bool VerdictReached { get; set; }
    }
}
