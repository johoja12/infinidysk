using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using Serilog;
using Serilog.Events;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Contexts;

internal sealed class YencFileValidationContext : IDisposable
{
    private static readonly byte[] DiagnosticKey = RandomNumberGenerator.GetBytes(32);
    private static readonly AsyncLocal<YencFileValidationContext?> Active = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The borrowed parent scope owns its disposal and must be restored, not disposed.")]
    private readonly YencFileValidationContext? _previous = Active.Value;
    private readonly NzbFile? _file;
    private readonly string[]? _segmentIds;
    private readonly string[][]? _segmentFallbacks;
    private readonly Lazy<Dictionary<string, int>> _positionIndex;
    private readonly bool _deferToPar2Proof;
    private readonly string? _fileName;
    private readonly bool _ownerLogsOutcome;
    private int _reportedMismatches;
    private string? _trackerKey;
    private readonly bool _firstSegmentProbe;

    private YencFileValidationContext(
        int expectedTotalParts,
        string stage = "Unknown",
        NzbFile? file = null,
        string[]? segmentIds = null,
        string[][]? segmentFallbacks = null,
        bool deferToPar2Proof = false,
        string? fileName = null,
        bool ownerLogsOutcome = false,
        Lazy<Dictionary<string, int>>? positionIndex = null,
        bool firstSegmentProbe = false)
    {
        _fileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        _ownerLogsOutcome = ownerLogsOutcome;
        ExpectedTotalParts = expectedTotalParts;
        _firstSegmentProbe = firstSegmentProbe;
        Stage = stage;
        _file = file;
        _segmentIds = segmentIds;
        _segmentFallbacks = segmentFallbacks;
        _positionIndex = positionIndex ?? new Lazy<Dictionary<string, int>>(CreatePositionIndex);
        _deferToPar2Proof = deferToPar2Proof;
        Active.Value = this;
    }

    public static YencFileValidationContext? Current => Active.Value;
    public static int? CurrentExpectedTotalParts => Current?.ExpectedTotalParts;
    public int ExpectedTotalParts { get; }
    public string Stage { get; }
    public static bool IsFirstSegmentProbe => Current?._firstSegmentProbe == true;

    // A file's first article always starts at byte 0, so a nonzero offset is another post's article.
    public static bool MatchesExpectedFile(UsenetYencHeader header, string? requestedId = null) =>
        IsFirstSegmentProbe ? header.PartOffset == 0 :
        Current?._deferToPar2Proof == true
        || CurrentExpectedTotalParts is not { } expectedTotalParts
        || (expectedTotalParts == 1 && header.TotalParts == 0)
        || (header.HasTotalParts == false && header.TotalParts == 0)
        || header.TotalParts == expectedTotalParts
        || (requestedId is not null
            && Current!.GetRequestDetails(requestedId).Position is { } position
            && HasSelfContradictoryTotal(header, position));

    /// <summary>
    /// True when <paramref name="header"/>'s yEnc total is consistent with a posted file of
    /// <paramref name="expectedTotalParts"/> articles read at <paramref name="position"/>, with
    /// the same tolerances <see cref="MatchesExpectedFile"/> applies inside a validation scope.
    /// </summary>
    internal static bool MatchesTotal(UsenetYencHeader header, int expectedTotalParts, int position) =>
        (expectedTotalParts == 1 && header.TotalParts == 0)
        || (header.HasTotalParts == false && header.TotalParts == 0)
        || header.TotalParts == expectedTotalParts
        || HasSelfContradictoryTotal(header, position);

    // Some posters obfuscate total/size; a part at its requested ordinal whose own geometry
    // cannot yield that total carries no evidence of belonging to another post.
    internal static bool HasSelfContradictoryTotal(UsenetYencHeader header, int position)
    {
        if (header.PartNumber != position || header.TotalParts <= 0 || header.FileSize <= 0 || header.PartSize <= 0)
            return false;

        long regularPartSize;
        if (position == 1)
        {
            if (header.PartOffset != 0) return false;
            regularPartSize = header.PartSize;
        }
        else
        {
            if (header.PartOffset <= 0 || header.PartOffset % (position - 1L) != 0) return false;
            regularPartSize = header.PartOffset / (position - 1L);
            if (header.PartSize > regularPartSize) return false;
        }

        return (header.FileSize - 1) / regularPartSize + 1 != header.TotalParts;
    }

    public static IDisposable Begin(int expectedTotalParts) =>
        new YencFileValidationContext(expectedTotalParts);

    public static IDisposable BeginFirstSegmentProbe() =>
        new YencFileValidationContext(1, "FirstSegment", firstSegmentProbe: true);

    public static IDisposable BeginSizeProbe(NzbFile file) =>
        new YencFileValidationContext(file.Segments.Count, "SizeProbe", file: file);

    public static IDisposable BeginStreaming(
        string[] segmentIds, string[][]? segmentFallbacks, string? fileName = null,
        Lazy<Dictionary<string, int>>? positionIndex = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "Streaming", segmentIds: segmentIds, segmentFallbacks: segmentFallbacks,
            deferToPar2Proof: Current?._deferToPar2Proof == true
                && ReferenceEquals(Current._segmentIds, segmentIds),
            fileName: fileName, positionIndex: positionIndex);

    /// <summary>
    /// Scope for sampled article-content verification of an imported file. Foreign answers
    /// are rejected per provider exactly as during streaming, but the verifier owns the
    /// operator-facing log line (one per file), so the per-file mismatch warnings stay quiet.
    /// </summary>
    internal static YencFileValidationContext BeginContentVerification(
        string[] segmentIds, string[][]? segmentFallbacks, string? fileName = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "ContentVerification", segmentIds: segmentIds,
            segmentFallbacks: segmentFallbacks, fileName: fileName, ownerLogsOutcome: true);

    /// <summary>
    /// Scope for PAR2 repair source reads: another post's article is rejected on each provider
    /// (so a provider holding this file's own article can still serve it) and otherwise
    /// surfaces as a missing article, which repair reconstructs from recovery blocks.
    /// </summary>
    internal static IDisposable BeginRepairSourceRead(string[] segmentIds, string? fileName = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "Par2RepairSource", segmentIds: segmentIds, fileName: fileName,
            ownerLogsOutcome: true);

    internal static IDisposable BeginBufferedPar2ProofRead(
        string[] segmentIds, string[][]? segmentFallbacks, string? fileName = null,
        Lazy<Dictionary<string, int>>? positionIndex = null) =>
        new YencFileValidationContext(
            segmentIds.Length, "BufferedPar2ProofRead", segmentIds: segmentIds,
            segmentFallbacks: segmentFallbacks, deferToPar2Proof: true, fileName: fileName, positionIndex: positionIndex);

    /// <summary>
    /// Identity of the active file for <see cref="MismatchedArticleTracker"/>: its first
    /// article plus its segment count, the same evidence a mismatch is judged against.
    /// Null when the context cannot identify a file or defers validation to PAR2.
    /// </summary>
    private string? TrackerKey
    {
        get
        {
            if (_deferToPar2Proof) return null;
            if (_trackerKey is not null) return _trackerKey;
            var anchor = _file is { Segments.Count: > 0 } file
                ? file.Segments[0].MessageId
                : _segmentIds is { Length: > 0 } ids ? ids[0] : null;
            return anchor is null
                ? null
                : _trackerKey = $"{NormalizeMessageId(anchor)}|{ExpectedTotalParts}";
        }
    }

    private string? DisplayFileName => _fileName ?? FetchAttributionContext.Current?.FileName;

    /// <summary>
    /// True when <paramref name="providerKey"/> already answered this file's
    /// <paramref name="requestedId"/> with another post's article. The provider walk skips
    /// it like a cached miss instead of downloading and discarding the same foreign body.
    /// </summary>
    public static bool IsKnownForeign(string requestedId, string providerKey) =>
        Current?.TrackerKey is { } fileKey
        && MismatchedArticleTracker.IsKnownForeign(fileKey, NormalizeMessageId(requestedId), providerKey);

    /// <summary>Records that a provider returned this file's own post for one of its articles.</summary>
    public static void RecordMatch()
    {
        if (Current?.TrackerKey is { } fileKey)
            MismatchedArticleTracker.RecordMatch(fileKey);
    }

    /// <summary>
    /// When enough of the active file's articles came back as another post on every
    /// provider that answered, returns the conclusive miss a reader should fail with
    /// instead of walking the providers again.
    /// </summary>
    public static UsenetForeignPostException? GetForeignFileFailure()
    {
        var current = Current;
        if (current?.TrackerKey is not { } fileKey
            || !MismatchedArticleTracker.TryGetFileVerdict(fileKey, out var foreignArticles))
            return null;
        var anchor = current._file is { Segments.Count: > 0 } file
            ? file.Segments[0].MessageId
            : current._segmentIds![0];
        return new UsenetForeignPostException(anchor, foreignArticles, current.ExpectedTotalParts);
    }

    internal static Lazy<Dictionary<string, int>> CreatePositionIndex(
        string[] segmentIds, string[][]? segmentFallbacks) =>
        new(() => BuildPositionIndex(segmentIds, segmentFallbacks));

    public (string? FileAnchor, int? Position, int? NzbNumber) GetRequestDetails(string requestedId)
    {
        if (_file is { Segments.Count: > 0 })
        {
            var anchor = _file.Segments[0].MessageId;
            return _positionIndex.Value.TryGetValue(NormalizeMessageId(requestedId), out var position)
                ? (anchor, position, _file.Segments[position - 1].Number)
                : (anchor, null, null);
        }

        if (_segmentIds is { Length: > 0 })
        {
            return (_segmentIds[0],
                _positionIndex.Value.TryGetValue(NormalizeMessageId(requestedId), out var position) ? position : null,
                null);
        }

        return (null, null, null);
    }

    /// <summary>
    /// How many provider answers inside this scope were rejected as another post's article.
    /// Lets a caller tell "some provider served a foreign body, the rest had nothing" apart
    /// from a plain miss, since the provider walk reports whichever outcome came last.
    /// </summary>
    public int ReportedMismatches => Volatile.Read(ref _reportedMismatches);

    public void ReportMismatch(string requestedId, string providerKey, int responseCode, UsenetYencHeader header)
    {
        Interlocked.Increment(ref _reportedMismatches);
        var (fileAnchor, position, nzbNumber) = GetRequestDetails(requestedId);
        var requestedIdKind = position is null ? "Unknown" : IsPrimaryRequest(requestedId, position) ? "Primary" : "Fallback";
        var geometryImpliedTotalParts = GetGeometryImpliedTotalParts(header);
        var fileRef = GetDiagnosticReference(fileAnchor);
        var articleRef = GetDiagnosticReference(requestedId);
        var providerRef = GetDiagnosticReference(providerKey);
        var returnedNameRef = GetDiagnosticReference(header.FileName);
        var key = $"yenc-mismatch/{Stage}/{fileRef}/{articleRef}/{providerRef}/{position}/{nzbNumber}/" +
                  $"{ExpectedTotalParts}/{responseCode}/{returnedNameRef}/{header.PartNumber}/{header.TotalParts}/" +
                  $"{header.FileSize}/{header.PartOffset}/{header.PartSize}/{header.LineLength}";
        // Per-article evidence is for maintainers; operators get one line per file below.
        ThrottledSegmentWarning.Write(
            LogEventLevel.Debug,
            key,
            MismatchDetailTemplate,
            Stage, fileRef, articleRef, providerRef, requestedIdKind, position, nzbNumber, ExpectedTotalParts, responseCode,
            geometryImpliedTotalParts,
            returnedNameRef, header.PartNumber, header.TotalParts, header.FileSize, header.PartOffset,
            header.PartSize, header.LineLength);

        var foreignArticles = 0;
        var rejections = 0;
        var verdictReached = false;
        if (TrackerKey is { } fileKey)
        {
            (foreignArticles, rejections, verdictReached) = MismatchedArticleTracker.RecordForeign(
                fileKey, NormalizeMessageId(requestedId), providerKey);
        }

        // Verification and repair scopes report one outcome line per file themselves.
        if (_ownerLogsOutcome) return;

        var fileName = DisplayFileName ?? "an unnamed file";
        if (verdictReached)
        {
            Log.Warning(
                "Stopping reads of {FileName}: {ForeignArticles} of its articles returned yEnc data from a different " +
                "post (yEnc total {ReturnedTotalParts} instead of {ExpectedTotalParts} parts) and no provider returned " +
                "this file's own post. The release looks overwritten or mis-posted, so reads now fail fast as missing " +
                "articles (which queues repair) instead of re-trying every provider for every article. " +
                "Stage: {Stage}; FileRef: {FileRef}",
                fileName, foreignArticles, header.TotalParts, ExpectedTotalParts, Stage, fileRef);
            return;
        }

        ThrottledSegmentWarning.Write(
            $"yenc-mismatch-file/{Stage}/{fileRef}",
            "Rejected yEnc articles from a different post while reading {FileName}: providers returned yEnc total " +
            "{ReturnedTotalParts} for a file with {ExpectedTotalParts} parts. Those providers are skipped for these " +
            "articles and other providers are tried. RejectedArticles: {RejectedArticles}; Rejections: {Rejections}; " +
            "Stage: {Stage}; FileRef: {FileRef}",
            fileName, header.TotalParts, ExpectedTotalParts, Math.Max(1, foreignArticles), Math.Max(1, rejections),
            Stage, fileRef);
    }

    internal const string MismatchDetailTemplate =
        "Rejected yEnc article because its parsed total differs from the active file segment count. " +
        "Stage: {Stage}; FileRef: {FileRef}; ArticleRef: {ArticleRef}; ProviderRef: {ProviderRef}; " +
        "RequestedIdKind: {RequestedIdKind}; RequestedSegmentPosition: {RequestedSegmentPosition}; " +
        "NzbSegmentNumber: {NzbSegmentNumber}; " +
        "ExpectedTotalParts: {ExpectedTotalParts}; ResponseCode: {ResponseCode}; " +
        "GeometryImpliedTotalParts: {GeometryImpliedTotalParts}; " +
        "ReturnedNameRef: {ReturnedNameRef}; ReturnedPartNumber: {ReturnedPartNumber}; " +
        "ReturnedTotalParts: {ReturnedTotalParts}; ReturnedFileSize: {ReturnedFileSize}; " +
        "ReturnedPartOffset: {ReturnedPartOffset}; ReturnedPartSize: {ReturnedPartSize}; " +
        "ReturnedLineLength: {ReturnedLineLength}; MetadataSource: ParsedYencHeader";

    private bool IsPrimaryRequest(string requestedId, int? position)
    {
        if (position is not > 0) return false;
        var index = position.Value - 1;
        if (_file is { } file && index < file.Segments.Count)
            return string.Equals(NormalizeMessageId(file.Segments[index].MessageId), NormalizeMessageId(requestedId),
                StringComparison.Ordinal);
        return _segmentIds is not null && index < _segmentIds.Length
            && string.Equals(NormalizeMessageId(_segmentIds[index]), NormalizeMessageId(requestedId),
                StringComparison.Ordinal);
    }

    private Dictionary<string, int> CreatePositionIndex()
    {
        if (_file is { } file)
            return BuildPositionIndex(file.GetSegmentIds(), file.GetSegmentFallbackIds());
        return BuildPositionIndex(_segmentIds ?? [], _segmentFallbacks);
    }

    private static Dictionary<string, int> BuildPositionIndex(string[] segmentIds, string[][]? segmentFallbacks)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var segmentIndex = 0; segmentIndex < segmentIds.Length; segmentIndex++)
            index.TryAdd(NormalizeMessageId(segmentIds[segmentIndex]), segmentIndex + 1);

        if (segmentFallbacks is not null)
        {
            for (var segmentIndex = 0; segmentIndex < Math.Min(segmentIds.Length, segmentFallbacks.Length); segmentIndex++)
            {
                if (segmentFallbacks[segmentIndex] is not { } fallbackIds) continue;
                foreach (var fallbackId in fallbackIds)
                    index.TryAdd(NormalizeMessageId(fallbackId), segmentIndex + 1);
            }
        }

        return index;
    }

    private static string NormalizeMessageId(string messageId) => new SegmentId(messageId).ToString();

    private static long? GetGeometryImpliedTotalParts(UsenetYencHeader header)
    {
        if (header.FileSize <= 0) return null;
        long regularPartSize;
        if (header.PartNumber == 1 && header.PartOffset == 0 && header.PartSize > 0)
        {
            regularPartSize = header.PartSize;
        }
        else if (header.PartNumber > 1 && header.PartOffset > 0
                 && header.PartOffset % (header.PartNumber - 1L) == 0)
        {
            regularPartSize = header.PartOffset / (header.PartNumber - 1L);
        }
        else
        {
            return null;
        }
        if (regularPartSize <= 0) return null;

        var impliedTotalParts = (header.FileSize - 1) / regularPartSize + 1;
        if (header.PartNumber <= 0 || header.PartNumber > impliedTotalParts)
            return null;

        var remainingFileSize = header.FileSize - header.PartOffset;
        if (remainingFileSize <= 0) return null;
        var expectedPartSize = Math.Min(regularPartSize, remainingFileSize);
        return header.PartSize == expectedPartSize ? impliedTotalParts : null;
    }

    internal static string? GetDiagnosticReference(string? value) => value is null
        ? null
        : Convert.ToHexString(HMACSHA256.HashData(DiagnosticKey, Encoding.UTF8.GetBytes(value)));

    public void Dispose() => Active.Value = _previous;
}
