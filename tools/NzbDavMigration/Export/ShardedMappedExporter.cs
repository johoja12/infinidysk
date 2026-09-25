using System.Security.Cryptography;
using System.Text;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbDavMigration.Export;

public sealed record ShardedMappedRootEvidence(
    ShardedMappedInventoryManifest Inventory,
    ShardedMappedRecoveryManifest Recovery,
    IReadOnlyList<LegacySourceRecoveryItem> Items,
    IReadOnlySet<Guid> DavItemIds,
    IReadOnlySet<string> PayloadSha256s);

public sealed record ShardedMappedPairEvidence(
    ShardedMappedRootEvidence Plex,
    ShardedMappedRootEvidence Special,
    int CombinedMappedRows,
    int CombinedRecoverableRows,
    decimal CombinedRecoveryFraction);

public sealed class ShardedMappedExporter
{
    public async Task<ShardedMappedRootEvidence> LoadRootAsync(
        string inventoryDirectory,
        string recoveryDirectory,
        CancellationToken cancellationToken = default)
    {
        var inventory = await ShardedMappedInventoryWriter.ReadManifestAsync(
            inventoryDirectory, cancellationToken).ConfigureAwait(false);
        var recovery = await ShardedMappedRecovery.ReadManifestAsync(
            recoveryDirectory, inventory, cancellationToken).ConfigureAwait(false);
        var items = new List<LegacySourceRecoveryItem>(inventory.RowCount);
        var ids = new HashSet<Guid>();
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < inventory.Shards.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await ShardedMappedInventoryWriter.ReadVerifiedShardAsync(
                inventoryDirectory, inventory, inventory.Shards[index], cancellationToken).ConfigureAwait(false);
            var master = await ShardedMappedRecovery.ReadVerifiedShardAsync(
                recoveryDirectory, recovery, recovery.Shards[index], source, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in source.Rows) ids.Add(row.DavItemId);
            foreach (var item in master.Items)
            {
                items.Add(item);
                if (item.Classification is "exact-direct" or "exact-archive")
                {
                    if (item.PayloadSha256 is null || item.SourceRelativePath is null
                        || item.IdentityKind is null || item.IdentityDigest is null || item.FileSize is null
                        || item.LegacyPath is null)
                        throw new InvalidDataException("Exact recovered row lacks payload provenance or identity.");
                    payloads.Add(item.PayloadSha256);
                }
            }
        }
        if (items.Count != recovery.TotalLinks)
            throw new InvalidDataException("Recovery shards omitted mapped rows.");
        return new ShardedMappedRootEvidence(inventory, recovery, items, ids, payloads);
    }

    public async Task<ShardedMappedPairEvidence> VerifyPairAsync(
        string plexInventory,
        string plexRecovery,
        string specialInventory,
        string specialRecovery,
        CancellationToken cancellationToken = default)
    {
        var plex = await LoadRootAsync(plexInventory, plexRecovery, cancellationToken).ConfigureAwait(false);
        var special = await LoadRootAsync(specialInventory, specialRecovery, cancellationToken)
            .ConfigureAwait(false);
        return ValidatePair(plex, special);
    }

    public static ShardedMappedPairEvidence ValidatePair(
        ShardedMappedRootEvidence plex,
        ShardedMappedRootEvidence special)
    {
        if (plex.Inventory.SourceRoot != "/mnt/plex" || special.Inventory.SourceRoot != "/mnt/special")
            throw new InvalidDataException("Cross-root verification requires the two configured legacy roots.");
        var combinedRows = checked(plex.Recovery.TotalLinks + special.Recovery.TotalLinks);
        var combinedRecovered = checked(plex.Recovery.RecoverableLinks + special.Recovery.RecoverableLinks);
        var combinedFraction = decimal.Divide(combinedRecovered, combinedRows);
        if (plex.Recovery.RecoverableFraction < 0.90m || special.Recovery.RecoverableFraction < 0.90m
            || combinedFraction < 0.90m)
            throw new InvalidDataException("Both roots and their combined mapped rows must pass 90% exact recovery.");
        if (plex.DavItemIds.Any(special.DavItemIds.Contains)
            || plex.PayloadSha256s.Any(special.PayloadSha256s.Contains))
            throw new InvalidDataException("The roots share mapped item IDs or NZB payloads; target reuse is required.");
        return new ShardedMappedPairEvidence(plex, special, combinedRows,
            combinedRecovered, combinedFraction);
    }

    public async Task VerifyFreshSourceAsync(
        ShardedMappedRootEvidence root,
        string blobRoot,
        string scratchParent,
        CancellationToken cancellationToken = default)
    {
        var parent = Path.GetFullPath(scratchParent);
        if (!Directory.Exists(parent) || new DirectoryInfo(parent).LinkTarget is not null)
            throw new InvalidDataException("Drift-check scratch parent must be an existing regular directory.");
        var scratch = Path.Join(parent, $".mapped-drift-{Guid.NewGuid():N}");
        try
        {
            var current = await new ShardedMappedInventoryWriter().WriteAsync(
                root.Inventory.SourceRoot, root.Inventory.LegacyIdsRoot, blobRoot, scratch,
                root.Inventory.Shards[0].RowCount, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (current.RowCount != root.Inventory.RowCount
                || current.RowsSha256 != root.Inventory.RowsSha256
                || current.Shards.Count != root.Inventory.Shards.Count)
                throw new InvalidDataException("LocalLinks or source symlinks changed since mapped inventory; re-inventory.");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    public async Task<int> ExportAsync(
        ShardedMappedPairEvidence pair,
        bool exportPlex,
        string legacyBlobRoot,
        string payloadRoot,
        string outputDirectory,
        int maxReleases = 250,
        long maxPayloadBytes = 4L * 1024 * 1024 * 1024,
        int? firstBatchMaxReleases = null,
        CancellationToken cancellationToken = default)
    {
        var scratchParent = Path.GetDirectoryName(Path.GetFullPath(outputDirectory))
            ?? throw new InvalidDataException("Export destination has no parent directory.");
        await VerifyFreshSourceAsync(pair.Plex, legacyBlobRoot, scratchParent, cancellationToken)
            .ConfigureAwait(false);
        await VerifyFreshSourceAsync(pair.Special, legacyBlobRoot, scratchParent, cancellationToken)
            .ConfigureAwait(false);
        return await ExportVerifiedAsync(pair, exportPlex, payloadRoot, outputDirectory,
            maxReleases, maxPayloadBytes, firstBatchMaxReleases, cancellationToken).ConfigureAwait(false);
    }

    // Only the public export path may call this after validating both roots and live source drift.
    internal async Task<int> ExportVerifiedAsync(
        ShardedMappedPairEvidence pair,
        bool exportPlex,
        string payloadRoot,
        string outputDirectory,
        int maxReleases = 250,
        long maxPayloadBytes = 4L * 1024 * 1024 * 1024,
        int? firstBatchMaxReleases = null,
        CancellationToken cancellationToken = default)
    {
        var root = exportPlex ? pair.Plex : pair.Special;
        var rootName = exportPlex ? "plex" : "special";
        var provenance = string.Join(':', pair.Plex.Inventory.RowsSha256,
            string.Join(':', pair.Plex.Recovery.Shards.Select(shard => shard.MasterSha256)),
            pair.Special.Inventory.RowsSha256,
            string.Join(':', pair.Special.Recovery.Shards.Select(shard => shard.MasterSha256)));
        // Each root owns an independent batch sequence. The pair proof is still
        // part of both digests, so either source changing invalidates export.
        var masterDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{rootName}:{provenance}")))
            .ToLowerInvariant();
        var releases = new List<FullRecoveryRelease>();
        foreach (var group in root.Items.Where(item => item.Classification is "exact-direct" or "exact-archive")
                     .GroupBy(item => item.PayloadSha256, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key is null || group.Any(item => item.SourceRelativePath is null))
                throw new InvalidDataException("Recoverable rows require payload provenance.");
            var relativePath = group.Select(item => item.SourceRelativePath!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).First();
            var path = OrphanCatalogueInputList.ResolveSafePath(payloadRoot, relativePath);
            AssertNoSymlinkAncestor(path, payloadRoot);
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Recovered NZB payload is missing or unsafe.");
            await using var payload = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (digest != group.Key)
                throw new InvalidDataException("Recovered NZB payload changed after catalogue sealing.");
            releases.Add(new FullRecoveryRelease(group.Key, group.Key, relativePath, info.Length,
                group.OrderBy(item => item.LibraryRelativePath, StringComparer.Ordinal).ToArray()));
        }
        var batches = new BatchPackagePlanner().Partition(
            releases, maxReleases, maxPayloadBytes, firstBatchMaxReleases);
        var output = Path.GetFullPath(outputDirectory);
        if (File.Exists(output) || (Directory.Exists(output)
            && new DirectoryInfo(output).LinkTarget is not null))
            throw new IOException("Batch export destination is a file or symbolic link.");
        Directory.CreateDirectory(output);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var expectedNames = batches.Select(batch => $"batch-{batch.BatchIndex + 1:D4}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(output).EnumerateFileSystemInfos())
            if (entry is not DirectoryInfo || entry.LinkTarget is not null
                || !expectedNames.Contains(entry.Name))
                throw new InvalidDataException("Batch export destination contains an unexpected entry.");
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchOutput = Path.Join(output, $"batch-{batch.BatchIndex + 1:D4}");
            if (Directory.Exists(batchOutput))
            {
                var existing = await new NzbDavPackageReader().ReadAsync(batchOutput, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.Manifest.PackageId != $"full-{masterDigest[..12]}-{batch.BatchIndex + 1:D4}"
                    || existing.Manifest.MasterManifestDigest != masterDigest
                    || existing.Manifest.BatchIndex != batch.BatchIndex
                    || existing.Manifest.BatchCount != batches.Count
                    || !existing.Manifest.Releases.Select(release => release.SourceReleaseId)
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(batch.Releases.Select(release => release.SourceReleaseId)
                            .Order(StringComparer.Ordinal), StringComparer.Ordinal)
                    || !existing.Manifest.Releases.SelectMany(release => release.Leaves)
                        .Select(leaf => (leaf.LegacyDavItemId, leaf.LegacyPath, leaf.FileSize,
                            leaf.IdentityKind, leaf.IdentityDigest))
                        .OrderBy(leaf => leaf.LegacyDavItemId)
                        .SequenceEqual(batch.Releases.SelectMany(release => release.Items)
                            .Select(item => (item.LegacyDavItemId, item.LegacyPath!, item.FileSize!.Value,
                                item.IdentityKind!, item.IdentityDigest))
                            .OrderBy(leaf => leaf.LegacyDavItemId))
                    || !existing.Manifest.Payloads.Select(payload => payload.Sha256)
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(batch.Releases.Select(release => release.PayloadSha256)
                            .Order(StringComparer.Ordinal), StringComparer.Ordinal)
                    || !existing.Manifest.SelectedLinks.Select(link =>
                            (link.LibraryRelativePath, link.OriginalTarget, link.LegacyDavItemId))
                        .OrderBy(link => link.LibraryRelativePath, StringComparer.Ordinal)
                        .SequenceEqual(batch.Releases.SelectMany(release => release.Items)
                            .Select(item => (item.LibraryRelativePath, item.OriginalTarget,
                                item.LegacyDavItemId))
                            .OrderBy(link => link.LibraryRelativePath, StringComparer.Ordinal)))
                    throw new InvalidDataException("Existing export batch disagrees with current sealed evidence.");
                continue;
            }
            var exportReleases = batch.Releases.Select(release =>
            {
                var names = NzbDavSourceNameResolver.FromLegacyPaths(
                    release.Items.Select(item => item.LegacyPath!));
                var leaves = release.Items.Select(item => new NzbDavExportLeaf(
                    item.LegacyDavItemId, item.LegacyPath!, item.FileSize!.Value,
                    release.SourceReleaseId, null, null, item.IdentityKind!, item.IdentityDigest,
                    "ready", null,
                    item.Classification == "exact-archive" ? item.IdentityKind : null,
                    item.Classification == "exact-archive" ? item.IdentityDigest : null)).ToArray();
                return new CanaryExportRelease(release.SourceReleaseId, null,
                    OrphanCatalogueInputList.ResolveSafePath(payloadRoot, release.PayloadRelativePath),
                    leaves, SourceFileName: names.FileName, SourceJobName: names.JobName);
            }).ToArray();
            var links = batch.Releases.SelectMany(release => release.Items)
                .Select(item => new NzbDavSelectedLibraryLink(
                    item.LibraryRelativePath, item.OriginalTarget, item.LegacyDavItemId)).ToArray();
            await new CanaryPackageWriter().WriteFullBatchAsync(
                new CanaryExportRequest($"full-{masterDigest[..12]}-{batch.BatchIndex + 1:D4}",
                    batchOutput, exportReleases, links),
                masterDigest, batch.BatchIndex, batches.Count, cancellationToken).ConfigureAwait(false);
        }
        return batches.Count;
    }

    private static void AssertNoSymlinkAncestor(string path, string blobRoot)
    {
        var root = Path.GetFullPath(blobRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null;
             directory = directory.Parent)
        {
            directory.Refresh();
            if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Recovered NZB payload has a symbolic-link ancestor.");
            if (directory.FullName == root) return;
        }
        throw new InvalidDataException("Recovered NZB payload escaped its source root.");
    }
}
