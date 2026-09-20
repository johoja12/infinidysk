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
        var ids = typeof(T) == typeof(DavRarFile)
            ? (await blobStore.ReadBlob<DavRarFile>(item.FileBlobId!.Value).ConfigureAwait(false))
                ?.RarParts.SelectMany(part => part.SegmentIds)
            : (await blobStore.ReadBlob<DavMultipartFile>(item.FileBlobId!.Value).ConfigureAwait(false))
                ?.Metadata.FileParts.SelectMany(part => part.SegmentIds);
        if (ids is null)
            return Missing(item);
        _ = Resolve(ids, document);
        var releaseDigest = NzbDavArticleIdentity.ComputeRelease(
            document.Files.Select(file => file.Segments.Select(ToSegment)));
        var innerPath = InnerPath(item.Path);
        return Present(item, NzbDavArticleIdentity.ArchiveMemberKind,
            NzbDavArticleIdentity.ComputeArchiveMember(releaseDigest, innerPath, item.FileSize ?? -1));
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
    private static string InnerPath(string path) => string.Join('/', path.Replace('\\', '/').Trim('/').Split('/').Skip(3));
    private static ImportedArticleIdentity Present(DavItem item, string kind, string digest) =>
        new(item.Id, item.Name, item.Path, item.FileSize, kind, digest, item.NzbBlobId);
    private static ImportedArticleIdentity Missing(DavItem item) =>
        new(item.Id, item.Name, item.Path, item.FileSize, null, null, item.NzbBlobId);
}
