using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;

namespace NzbDavMigration.Recovery;

public sealed class LegacySourceRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<LegacySourceRecoveryReport> RecoverAsync(
        IReadOnlyList<LegacyInventoryCandidate> inventory,
        OrphanCatalogueStore catalogue,
        CancellationToken cancellationToken = default)
    {
        var items = new List<LegacySourceRecoveryItem>(inventory.Count);
        foreach (var candidate in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await RecoverOneAsync(candidate, catalogue, cancellationToken).ConfigureAwait(false));
        }
        var recoverable = items.Count(item => item.Classification is "exact-direct" or "exact-archive");
        var fraction = items.Count == 0 ? 0m : decimal.Divide(recoverable, items.Count);
        return new LegacySourceRecoveryReport(LegacySourceRecoveryReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow, items.Count, recoverable, fraction, items);
    }

    public async Task<LegacyRecoveryWriteResult> WriteAsync(
        IReadOnlyList<LegacyInventoryCandidate> inventory,
        OrphanCatalogueStore catalogue,
        string outputDirectory,
        decimal minimumCoverage,
        CancellationToken cancellationToken = default)
    {
        if (minimumCoverage is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumCoverage));
        var report = await RecoverAsync(inventory, catalogue, cancellationToken).ConfigureAwait(false);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException($"Recovery output destination already exists: {output}");
        var parent = Path.GetDirectoryName(output)
            ?? throw new InvalidDataException("Recovery output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var stage = Path.Join(parent, $".{Path.GetFileName(output)}.tmp-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var master = new FullRecoveryMasterManifest(FullRecoveryMasterManifest.CurrentSchemaVersion,
                report.CreatedAt, report.TotalLinks, report.RecoverableLinks, report.RecoverableFraction, report.Items);
            var exclusions = report.Items.Where(item =>
                item.Classification is not ("exact-direct" or "exact-archive")).ToArray();
            await WriteJsonAsync(Path.Join(stage, "recovery.json"), report, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Join(stage, "master-manifest.json"), master, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Join(stage, "exclusions.json"), exclusions, cancellationToken).ConfigureAwait(false);
            var sums = new StringBuilder();
            foreach (var name in new[] { "exclusions.json", "master-manifest.json", "recovery.json" })
            {
                await using var input = File.OpenRead(Path.Join(stage, name));
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)
                    .ConfigureAwait(false)).ToLowerInvariant();
                sums.Append(digest).Append("  ").Append(name).Append('\n');
            }
            await WriteBytesAsync(Path.Join(stage, "SHA256SUMS"), Encoding.UTF8.GetBytes(sums.ToString()),
                cancellationToken).ConfigureAwait(false);
            Directory.Move(stage, output);
            return new LegacyRecoveryWriteResult(report, report.RecoverableFraction >= minimumCoverage);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    private static async Task<LegacySourceRecoveryItem> RecoverOneAsync(
        LegacyInventoryCandidate candidate,
        OrphanCatalogueStore catalogue,
        CancellationToken cancellationToken)
    {
        if (candidate.Item is not { } row || candidate.ExclusionReason is not null)
            return Missing(candidate, "excluded", candidate.ExclusionReason ?? "missing-database-row");
        string[] requiredIds;
        try { requiredIds = RequiredArticleIds(row); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Missing(candidate, "missing-articles", "article-evidence-unavailable");
        }
        if (requiredIds.Length == 0)
            return Missing(candidate, "missing-articles", "article-evidence-unavailable");

        var possible = await catalogue.FindBlobsContainingAllAsync(requiredIds, cancellationToken)
            .ConfigureAwait(false);
        var matches = possible.Select(blob => Match(row, requiredIds, blob))
            .Where(match => match is not null).Select(match => match!).ToArray();
        var logical = matches.Where(match => match.Blob.Sha256 is not null)
            .GroupBy(match => match.Blob.Sha256!, StringComparer.Ordinal)
            .Select(group => group.OrderBy(match => match.Blob.RelativePath, StringComparer.Ordinal).First())
            .OrderBy(match => match.Blob.Sha256, StringComparer.Ordinal).ToArray();
        if (logical.Length == 0) return Missing(candidate, "missing-articles", "no-exact-article-payload");
        if (logical.Length > 1) return Missing(candidate, "ambiguous-payload", "multiple-distinct-payloads");
        var exact = logical[0];
        return new LegacySourceRecoveryItem(candidate.LibraryRelativePath, candidate.OriginalTarget,
            candidate.LegacyDavItemId, row.Type == 3 ? "exact-direct" : "exact-archive", null,
            exact.Blob.RelativePath, exact.Blob.Sha256, exact.IdentityKind, exact.IdentityDigest, row.FileSize);
    }

    private static RecoveryMatch? Match(
        LegacyDavItemRow row,
        IReadOnlyList<string> requiredIds,
        OrphanCatalogueBlob blob)
    {
        var document = BuildDocument(blob);
        if (row.Type == 3)
        {
            var expected = requiredIds.Select(Normalize).ToArray();
            var orderedFileMatch = blob.Articles.GroupBy(article => article.FileOrdinal)
                .Any(group => group.OrderBy(article => article.SegmentOrdinal)
                    .Select(article => Normalize(article.MessageId)).SequenceEqual(expected, StringComparer.Ordinal));
            if (!orderedFileMatch) return null;
        }
        var computedRelease = document.Files.Count == 0 ? null :
            NzbWebDAV.UsenetMigration.NzbDav.NzbDavArticleIdentity.ComputeRelease(document.Files.Select(file =>
                file.Segments.Select(segment => new NzbWebDAV.UsenetMigration.NzbDav.NzbDavArticleSegment(
                    segment.Number, segment.Bytes, segment.MessageId))));
        if (!string.Equals(blob.ReleaseDigest, computedRelease, StringComparison.Ordinal)) return null;
        var leaf = new LegacyIdentityExtractor().Extract(row with { NzbBlobId = Guid.Empty }, document,
            blob.Sha256 ?? blob.RelativePath);
        return leaf.ExtractionStatus == "ready" && leaf.IdentityDigest is not null
            ? new RecoveryMatch(blob, leaf.IdentityKind, leaf.IdentityDigest)
            : null;
    }

    private static NzbDocument BuildDocument(OrphanCatalogueBlob blob)
    {
        var document = new NzbDocument();
        foreach (var group in blob.Articles.GroupBy(article => article.FileOrdinal).OrderBy(group => group.Key))
        {
            var file = new NzbFile { Subject = string.Empty };
            foreach (var article in group.OrderBy(article => article.SegmentOrdinal))
                file.Segments.Add(new NzbSegment
                    { MessageId = article.MessageId, Number = article.SegmentOrdinal, Bytes = article.SegmentBytes });
            document.Files.Add(file);
        }
        return document;
    }

    private static string[] RequiredArticleIds(LegacyDavItemRow row) => row.Type switch
    {
        3 => JsonSerializer.Deserialize<string[]>(row.NzbSegmentsJson ?? "", JsonOptions)
             ?? throw new InvalidDataException("Direct article metadata is missing."),
        4 => (JsonSerializer.Deserialize<DavRarFile.RarPart[]>(row.RarPartsJson ?? "", JsonOptions)
              ?? throw new InvalidDataException("RAR article metadata is missing."))
            .SelectMany(part => part.SegmentIds).ToArray(),
        6 => MultipartIds(row.MultipartMetadataJson),
        _ => throw new InvalidDataException("Unsupported legacy item type."),
    };

    private static string[] MultipartIds(string? json)
    {
        var metadata = JsonSerializer.Deserialize<DavMultipartFile.Meta>(json ?? "", JsonOptions)
            ?? throw new InvalidDataException("Multipart article metadata is missing.");
        if ((metadata.PendingParts?.Length ?? 0) != 0)
            throw new InvalidDataException("Multipart article metadata is unresolved.");
        return metadata.FileParts.SelectMany(part => part.SegmentIds).ToArray();
    }

    private static LegacySourceRecoveryItem Missing(
        LegacyInventoryCandidate candidate,
        string classification,
        string? reason) =>
        new(candidate.LibraryRelativePath, candidate.OriginalTarget, candidate.LegacyDavItemId,
            classification, reason, null, null, null, null, candidate.Item?.FileSize);

    private static string Normalize(string value) => value.Trim().TrimStart('<').TrimEnd('>').Trim();

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        await WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken)
            .ConfigureAwait(false);

    private static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = CanaryPackageWriter.CreatePrivateFile(path);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Recovery evidence must be durable before handoff.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private sealed record RecoveryMatch(
        OrphanCatalogueBlob Blob,
        string IdentityKind,
        string IdentityDigest);
}
