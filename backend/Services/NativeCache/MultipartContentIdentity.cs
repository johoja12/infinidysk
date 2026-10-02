using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Content-defining identity of a multipart (RAR/7z/split) file. Unlike a hash of the raw
/// blob, it ignores metadata that lazy RAR resolution fills in for bytes the blob already
/// references, so resolving a volume neither invalidates active readers nor starts a new
/// Native Cache generation. A real source change (different segments, byte ranges,
/// encryption or member size) yields a different identity.
/// </summary>
public static class MultipartContentIdentity
{
    private const string Domain = "nzbdav:multipart-content:1";

    /// <summary>
    /// The identity carried by <paramref name="meta"/>: the value captured before lazy
    /// resolution first rewrote it, otherwise one computed from its current parts.
    /// </summary>
    public static string Get(DavMultipartFile.Meta meta) =>
        string.IsNullOrEmpty(meta.ContentIdentity) ? Compute(meta) : meta.ContentIdentity;

    /// <summary>
    /// Hashes the parts as they are now. Lazy RAR resolution changes these fields, so callers
    /// rewriting a lazy meta must carry <see cref="Get"/> forward rather than recompute it.
    /// Split-state hints, fallbacks, PAR2 proofs and the archive password are excluded:
    /// they do not define the decoded bytes (fallbacks are covered by repair revisions,
    /// and encryption is covered by the derived AES parameters).
    /// </summary>
    public static string Compute(DavMultipartFile.Meta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, Domain);
        AppendString(hash, meta.PathInArchive);
        AppendLong(hash, meta.ExpectedFileSize ?? -1);

        if (meta.AesParams is { } aes)
        {
            AppendString(hash, "aes");
            AppendLong(hash, aes.DecodedSize);
            AppendBytes(hash, aes.Iv);
            AppendBytes(hash, aes.Key);
        }
        else
        {
            AppendString(hash, "plain");
        }

        var fileParts = meta.FileParts ?? [];
        AppendLong(hash, fileParts.Length);
        foreach (var part in fileParts)
        {
            AppendSegments(hash, part.SegmentIds);
            AppendRange(hash, part.SegmentIdByteRange);
            AppendRange(hash, part.FilePartByteRange);
            var segmentRanges = part.SegmentByteRanges;
            AppendLong(hash, segmentRanges?.Length ?? -1);
            foreach (var range in segmentRanges ?? []) AppendRange(hash, range);
        }

        var pendingParts = meta.PendingParts ?? [];
        AppendLong(hash, pendingParts.Length);
        foreach (var part in pendingParts)
        {
            AppendSegments(hash, part.SegmentIds);
            AppendRange(hash, part.SegmentIdByteRange);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendSegments(IncrementalHash hash, string[]? segmentIds)
    {
        AppendLong(hash, segmentIds?.Length ?? -1);
        foreach (var segmentId in segmentIds ?? []) AppendString(hash, segmentId);
    }

    private static void AppendRange(IncrementalHash hash, LongRange? range)
    {
        AppendLong(hash, range?.StartInclusive ?? -1);
        AppendLong(hash, range?.EndExclusive ?? -1);
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            AppendLong(hash, -1);
            return;
        }

        AppendBytes(hash, Encoding.UTF8.GetBytes(value));
    }

    private static void AppendBytes(IncrementalHash hash, byte[]? value)
    {
        AppendLong(hash, value?.Length ?? -1);
        if (value is { Length: > 0 }) hash.AppendData(value);
    }

    private static void AppendLong(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }
}
