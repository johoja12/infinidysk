using System.Text.Json;
using NzbDavMigration.Legacy;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbDavMigration.Export;

public sealed class LegacyIdentityExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public NzbDavExportLeaf Extract(
        LegacyDavItemRow row,
        NzbDocument document,
        string sourceReleaseId)
    {
        if (row.FileSize is null or < 0)
            return Excluded(row, sourceReleaseId, "missing-size");
        if (row.NzbBlobId is null)
            return Excluded(row, sourceReleaseId, "missing-nzb-blob-id");

        try
        {
            return row.Type switch
            {
                3 => Direct(row, document, sourceReleaseId),
                4 => Archive(row, document, sourceReleaseId, ParseRarParts(row.RarPartsJson, document)),
                6 => Archive(row, document, sourceReleaseId, ParseMultipartParts(row.MultipartMetadataJson, document)),
                _ => Excluded(row, sourceReleaseId, $"unsupported-legacy-type-{row.Type}"),
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException)
        {
            return Excluded(row, sourceReleaseId, "identity-unavailable: invalid or unmatched article metadata");
        }
    }

    private static NzbDavExportLeaf Direct(
        LegacyDavItemRow row,
        NzbDocument document,
        string sourceReleaseId)
    {
        var ids = JsonSerializer.Deserialize<string[]>(row.NzbSegmentsJson ?? "", JsonOptions)
                  ?? throw new InvalidDataException("Direct-file segment metadata is missing.");
        var segments = ResolveSegments(ids, document);
        return Ready(row, sourceReleaseId, NzbDavArticleIdentity.DirectKind,
            NzbDavArticleIdentity.ComputeDirect(segments));
    }

    private static NzbDavExportLeaf Archive(
        LegacyDavItemRow row,
        NzbDocument document,
        string sourceReleaseId,
        IReadOnlyList<NzbDavArchivePartIdentity> parts)
    {
        var releaseSegments = document.Files.Select(file => file.Segments.Select(ToIdentitySegment));
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(releaseSegments);
        return Ready(row, sourceReleaseId, NzbDavStableArchiveIdentity.Kind,
            NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, row.FileSize!.Value));
    }

    private static NzbDavArchivePartIdentity[] ParseRarParts(string? json, NzbDocument document)
    {
        var parts = JsonSerializer.Deserialize<DavRarFile.RarPart[]>(json ?? "", JsonOptions)
                    ?? throw new InvalidDataException("RAR metadata is missing.");
        return parts.Select(part => new NzbDavArchivePartIdentity(
            ResolveSegments(part.SegmentIds, document),
            SegmentStart: 0,
            SegmentLength: RarSegmentLength(part),
            FileStart: part.Offset,
            FileLength: part.ByteCount)).ToArray();
    }

    private static NzbDavArchivePartIdentity[] ParseMultipartParts(string? json, NzbDocument document)
    {
        var metadata = JsonSerializer.Deserialize<DavMultipartFile.Meta>(json ?? "", JsonOptions)
                       ?? throw new InvalidDataException("Multipart metadata is missing.");
        if ((metadata.PendingParts?.Length ?? 0) != 0)
            throw new InvalidDataException("Multipart metadata contains unresolved pending parts.");
        return metadata.FileParts.Select(part => MultipartPart(part, document)).ToArray();
    }

    private static NzbDavArchivePartIdentity MultipartPart(
        DavMultipartFile.FilePart part,
        NzbDocument document)
    {
        if (part.SegmentIdByteRange is null || part.FilePartByteRange is null)
            throw new InvalidDataException("Multipart metadata contains a missing byte range.");
        return new NzbDavArchivePartIdentity(
            ResolveSegments(part.SegmentIds, document),
            part.SegmentIdByteRange.StartInclusive,
            part.SegmentIdByteRange.Count,
            part.FilePartByteRange.StartInclusive,
            part.FilePartByteRange.Count);
    }

    private static long RarSegmentLength(DavRarFile.RarPart part)
    {
        try
        {
            return Math.Max(part.PartSize, checked(part.Offset + part.ByteCount));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("RAR metadata contains an overflowing byte range.", exception);
        }
    }

    private static NzbDavArticleSegment[] ResolveSegments(
        IEnumerable<string> ids,
        NzbDocument document)
    {
        var groups = document.Files.SelectMany(file => file.Segments)
            .GroupBy(segment => NormalizeMessageId(segment.MessageId), StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Count() != 1))
            throw new InvalidDataException("Source NZB contains duplicate article IDs.");
        var index = groups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var resolved = ids.Select(id => index.TryGetValue(NormalizeMessageId(id), out var segment)
                ? ToIdentitySegment(segment)
                : throw new KeyNotFoundException("An article is absent from the source NZB."))
            .ToArray();
        if (resolved.Length == 0)
            throw new InvalidDataException("Article metadata is empty.");
        return resolved;
    }

    private static string NormalizeMessageId(string id) => id.Trim().TrimStart('<').TrimEnd('>').Trim();

    private static NzbDavArticleSegment ToIdentitySegment(NzbSegment segment) =>
        new(segment.Number, segment.Bytes, segment.MessageId);

    private static NzbDavExportLeaf Ready(
        LegacyDavItemRow row,
        string sourceReleaseId,
        string kind,
        string digest) =>
        new(row.Id, row.Path, row.FileSize!.Value, sourceReleaseId, row.HistoryItemId,
            row.NzbBlobId, kind, digest, "ready", null);

    private static NzbDavExportLeaf Excluded(
        LegacyDavItemRow row,
        string sourceReleaseId,
        string reason) =>
        new(row.Id, row.Path, row.FileSize ?? 0, sourceReleaseId, row.HistoryItemId,
            row.NzbBlobId, "unavailable", null, "excluded", reason);
}
