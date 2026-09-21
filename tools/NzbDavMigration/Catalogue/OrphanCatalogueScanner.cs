using System.Security.Cryptography;
using System.Xml;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbDavMigration.Catalogue;

public sealed class OrphanCatalogueScanner(
    long perFileLimit = 64 * 1024 * 1024,
    Func<string, CancellationToken, Task>? afterRead = null,
    Func<string, CancellationToken, Task>? afterCommit = null)
{
    public async Task<OrphanCatalogueSummary> ScanAsync(
        string blobRoot,
        string frozenInventoryPath,
        OrphanCatalogueStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var root = Path.GetFullPath(blobRoot);
        var (inventory, inputDigest) = await OrphanCatalogueInputList.ReadAsync(
            frozenInventoryPath, cancellationToken).ConfigureAwait(false);
        await store.BeginAsync(inputDigest, cancellationToken).ConfigureAwait(false);
        var state = await store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(state.Status, "complete", StringComparison.Ordinal))
            return await store.BuildSummaryAsync(inputDigest, cancellationToken).ConfigureAwait(false);

        foreach (var input in inventory.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await store.ContainsSnapshotAsync(input, cancellationToken).ConfigureAwait(false)) continue;
            var blob = await ScanOneAsync(root, input, cancellationToken).ConfigureAwait(false);
            await store.UpsertAsync(blob, cancellationToken).ConfigureAwait(false);
            if (afterCommit is not null)
                await afterCommit(input.RelativePath, cancellationToken).ConfigureAwait(false);
        }

        var summary = await store.BuildSummaryAsync(inputDigest, cancellationToken).ConfigureAwait(false);
        if (summary.BlobCount != inventory.Items.Count)
            throw new InvalidOperationException("Catalogue scan did not produce one terminal row per frozen input.");
        await store.SealAsync(summary, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    private async Task<OrphanCatalogueBlob> ScanOneAsync(
        string root,
        OrphanCatalogueInputItem input,
        CancellationToken cancellationToken)
    {
        var path = OrphanCatalogueInputList.ResolveSafePath(root, input.RelativePath);
        try
        {
            var before = new FileInfo(path);
            before.Refresh();
            if (IsLink(before) || HasLinkAncestor(path, root)) return Failure(input, "symlink");
            if (!before.Exists) return Failure(input, "missing");
            if (before.Length != input.Length || before.LastWriteTimeUtc.Ticks != input.MtimeTicks)
                return Failure(input, "changed");
            if (before.Length > perFileLimit) return Failure(input, "oversized");
            if (before.Length > int.MaxValue) return Failure(input, "oversized");

            byte[] bytes;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var opened = new FileInfo(path);
                opened.Refresh();
                if (IsLink(opened) || HasLinkAncestor(path, root)) return Failure(input, "symlink");
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (afterRead is not null) await afterRead(path, cancellationToken).ConfigureAwait(false);
            var after = new FileInfo(path);
            after.Refresh();
            if (IsLink(after) || HasLinkAncestor(path, root)) return Failure(input, "symlink");
            if (!after.Exists || after.Length != input.Length || after.LastWriteTimeUtc.Ticks != input.MtimeTicks)
                return Failure(input, "changed");

            try
            {
                await using var validation = new MemoryStream(bytes, writable: false);
                await NzbXmlSecurity.ValidateAsync(validation, bytes.LongLength * 4 + 1024, cancellationToken)
                    .ConfigureAwait(false);
                await using var parse = new MemoryStream(bytes, writable: false);
                var document = await NzbDocument.LoadAsync(parse, cancellationToken).ConfigureAwait(false);
                var articles = document.Files.SelectMany((file, fileOrdinal) =>
                    file.Segments.Select((segment, segmentOrdinal) => new OrphanCatalogueArticle(
                        NormalizeMessageId(segment.MessageId), fileOrdinal,
                        segment.Number ?? segmentOrdinal, segment.Bytes))).ToArray();
                string? releaseDigest = null;
                if (document.Files.Count > 0)
                {
                    if (document.Files.Any(file => file.Segments.Count == 0))
                        return Failure(input, "invalid-articles", sha256);
                    releaseDigest = NzbDavArticleIdentity.ComputeRelease(document.Files.Select(file =>
                        file.Segments.Select(segment => new NzbDavArticleSegment(
                            segment.Number, segment.Bytes, segment.MessageId))));
                }
                return new OrphanCatalogueBlob(input.RelativePath, input.Length, input.MtimeTicks,
                    sha256, "valid", null, releaseDigest, articles);
            }
            catch (Exception exception) when (exception is XmlException or InvalidDataException)
            {
                return Failure(input, "invalid-xml", sha256);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(input, "io-error");
        }
    }

    private static OrphanCatalogueBlob Failure(
        OrphanCatalogueInputItem input,
        string failureClass,
        string? sha256 = null) =>
        new(input.RelativePath, input.Length, input.MtimeTicks, sha256, "failed", failureClass, null, []);

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static bool HasLinkAncestor(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null;
             directory = directory.Parent)
        {
            directory.Refresh();
            if (IsLink(directory)) return true;
            if (string.Equals(directory.FullName, normalizedRoot, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static string NormalizeMessageId(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('<') && normalized.EndsWith('>') && normalized.Length >= 2)
            normalized = normalized[1..^1].Trim();
        if (normalized.Length == 0) throw new InvalidDataException("NZB contains an empty article identifier.");
        return normalized;
    }
}
