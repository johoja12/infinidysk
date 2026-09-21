using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed class DavItemArticleIdentityReader(IBlobStore blobStore)
{
    public async Task<ImportedArticleIdentity> ReadAsync(
        DavItem item,
        CancellationToken cancellationToken = default)
    {
        if (item.NzbBlobId is null || item.FileBlobId is null)
            return Missing(item);
        await using var nzbStream = blobStore.ReadBlob(item.NzbBlobId.Value);
        if (nzbStream is null)
            return Missing(item);
        var document = await NzbDocument.LoadAsync(nzbStream, cancellationToken).ConfigureAwait(false);
        try
        {
            return item.SubType switch
            {
                DavItem.ItemSubType.NzbFile => await ReadDirectAsync(item, document).ConfigureAwait(false),
                DavItem.ItemSubType.RarFile => await ReadArchiveAsync<DavRarFile>(item, document).ConfigureAwait(false),
                DavItem.ItemSubType.MultipartFile => await ReadArchiveAsync<DavMultipartFile>(item, document).ConfigureAwait(false),
                _ => Missing(item),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException)
        {
            return Missing(item);
        }
    }

    private async Task<ImportedArticleIdentity> ReadDirectAsync(DavItem item, NzbDocument document)
    {
        var metadata = await blobStore.ReadBlob<DavNzbFile>(item.FileBlobId!.Value).ConfigureAwait(false);
        if (metadata is null)
            return Missing(item);
        var segments = Resolve(metadata.SegmentIds, document);
        return Present(item, NzbDavArticleIdentity.DirectKind, NzbDavArticleIdentity.ComputeDirect(segments));
    }

    private async Task<ImportedArticleIdentity> ReadArchiveAsync<T>(DavItem item, NzbDocument document)
    {
        IReadOnlyList<NzbDavArchivePartIdentity>? parts;
        if (typeof(T) == typeof(DavRarFile))
        {
            var metadata = await blobStore.ReadBlob<DavRarFile>(item.FileBlobId!.Value).ConfigureAwait(false);
            parts = metadata?.RarParts.Select(part => RarPart(part, document)).ToArray();
        }
        else
        {
            var metadata = await blobStore.ReadBlob<DavMultipartFile>(item.FileBlobId!.Value).ConfigureAwait(false);
            if (metadata is null || (metadata.Metadata.PendingParts?.Length ?? 0) != 0)
                return Missing(item);
            parts = metadata.Metadata.FileParts.Select(part => MultipartPart(part, document)).ToArray();
        }
        if (parts is null)
            return Missing(item);
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(
            document.Files.Select(file => file.Segments.Select(ToSegment)));
        return Present(item, NzbDavStableArchiveIdentity.Kind,
            NzbDavStableArchiveIdentity.Compute(releaseDigest, parts, item.FileSize ?? -1));
    }

    private static NzbDavArchivePartIdentity RarPart(DavRarFile.RarPart part, NzbDocument document) =>
        new(
            Resolve(part.SegmentIds, document),
            SegmentStart: 0,
            SegmentLength: RarSegmentLength(part),
            FileStart: part.Offset,
            FileLength: part.ByteCount);

    private static NzbDavArchivePartIdentity MultipartPart(
        DavMultipartFile.FilePart part,
        NzbDocument document)
    {
        if (part.SegmentIdByteRange is null || part.FilePartByteRange is null)
            throw new InvalidDataException("Multipart metadata contains a missing byte range.");
        return new NzbDavArchivePartIdentity(
            Resolve(part.SegmentIds, document),
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

    private static NzbDavArticleSegment[] Resolve(IEnumerable<string> ids, NzbDocument document)
    {
        var index = document.Files.SelectMany(file => file.Segments)
            .ToDictionary(segment => Normalize(segment.MessageId), StringComparer.Ordinal);
        return ids.Select(id => index.TryGetValue(Normalize(id), out var segment)
                ? ToSegment(segment)
                : throw new KeyNotFoundException(id))
            .ToArray();
    }

    private static NzbDavArticleSegment ToSegment(NzbSegment segment) =>
        new(segment.Number, segment.Bytes, segment.MessageId);
    private static string Normalize(string value) => value.Trim().TrimStart('<').TrimEnd('>').Trim();
    private static ImportedArticleIdentity Present(DavItem item, string kind, string digest) =>
        new(item.Id, item.Name, item.Path, item.FileSize, kind, digest, item.NzbBlobId);
    private static ImportedArticleIdentity Missing(DavItem item) =>
        new(item.Id, item.Name, item.Path, item.FileSize, null, null, item.NzbBlobId);
}
