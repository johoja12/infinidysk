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
                4 => Archive(row, document, sourceReleaseId, ParseRarSegments(row.RarPartsJson)),
                6 => Archive(row, document, sourceReleaseId, ParseMultipartSegments(row.MultipartMetadataJson)),
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
        IReadOnlyList<string> contributingIds)
    {
        _ = ResolveSegments(contributingIds, document);
        var releaseSegments = document.Files.Select(file => file.Segments.Select(ToIdentitySegment));
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(releaseSegments);
        var innerPath = NormalizeArchivePath(row.Path, row.ReleaseRootPath);
        return Ready(row, sourceReleaseId, NzbDavArticleIdentity.ArchiveMemberKind,
            NzbDavArticleIdentity.ComputeArchiveMember(releaseDigest, innerPath, row.FileSize!.Value));
    }

    private static string[] ParseRarSegments(string? json)
    {
        var parts = JsonSerializer.Deserialize<DavRarFile.RarPart[]>(json ?? "", JsonOptions)
                    ?? throw new InvalidDataException("RAR metadata is missing.");
        return parts.SelectMany(part => part.SegmentIds).ToArray();
    }

    private static string[] ParseMultipartSegments(string? json)
    {
        var metadata = JsonSerializer.Deserialize<DavMultipartFile.Meta>(json ?? "", JsonOptions)
                       ?? throw new InvalidDataException("Multipart metadata is missing.");
        return metadata.FileParts.SelectMany(part => part.SegmentIds).ToArray();
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

    private static string NormalizeArchivePath(string path, string? releaseRoot)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(releaseRoot))
            throw new InvalidDataException("Release directory ancestry is missing.");
        var prefix = releaseRoot.Replace('\\', '/').TrimEnd('/') + "/";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("File is outside the proven release directory.");
        var relative = normalized[prefix.Length..];
        if (relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Archive member path is invalid.");
        return relative;
    }

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
