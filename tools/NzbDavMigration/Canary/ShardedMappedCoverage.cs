using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Recovery;

namespace NzbDavMigration.Canary;

public sealed class ShardedMappedCoverage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<CanaryCoverageWriteResult> WriteAsync(
        string inventoryDirectory,
        string recoveryDirectory,
        string blobRoot,
        string parallelLibraryRoot,
        string journalsDirectory,
        string outputDirectory,
        decimal minimumCoverage = 0.90m,
        CancellationToken cancellationToken = default)
    {
        if (minimumCoverage < 0m || minimumCoverage > 1m)
            throw new InvalidDataException("Mapped final coverage minimum must be between 0 and 1.00.");
        var root = await new ShardedMappedExporter().LoadRootAsync(
            inventoryDirectory, recoveryDirectory, cancellationToken).ConfigureAwait(false);
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputDirectory))
            ?? throw new InvalidDataException("Coverage destination has no parent directory.");
        if (!Directory.Exists(parent) || new DirectoryInfo(parent).LinkTarget is not null)
            throw new InvalidDataException("Coverage scratch parent must be an existing regular directory.");
        var scratch = Path.Join(parent, $".mapped-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var currentDirectory = Path.Join(scratch, "current");
            var current = await new ShardedMappedInventoryWriter().WriteAsync(
                root.Inventory.SourceRoot, root.Inventory.LegacyIdsRoot, blobRoot, currentDirectory,
                root.Inventory.Shards[0].RowCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            var initialAllowlist = await ShardedMappedInventoryWriter.ReadAllowlistAsync(
                inventoryDirectory, root.Inventory, cancellationToken).ConfigureAwait(false);
            var finalAllowlist = await ShardedMappedInventoryWriter.ReadAllowlistAsync(
                currentDirectory, current, cancellationToken).ConfigureAwait(false);
            var initial = CompactInventory(root.Inventory, initialAllowlist);
            var final = CompactInventory(current, finalAllowlist);
            var masterPath = Path.Join(scratch, "master.json");
            var master = new FullRecoveryMasterManifest(
                FullRecoveryMasterManifest.CurrentSchemaVersion, DateTimeOffset.UtcNow,
                root.Recovery.TotalLinks, root.Recovery.RecoverableLinks,
                root.Recovery.RecoverableFraction, root.Items,
                new MappedSourceProof(root.Inventory.SourceRoot, root.Inventory.LegacyIdsRoot,
                    root.Inventory.RowCount, root.Inventory.RowsSha256));
            await using (var stream = CanaryPackageWriter.CreatePrivateFile(masterPath))
                await JsonSerializer.SerializeAsync(stream, master, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            return await new CanaryCoverageReporter().WriteAsync(
                root.Inventory.SourceRoot, parallelLibraryRoot, masterPath, masterPath,
                journalsDirectory, outputDirectory, minimumCoverage, initialMapped: initial,
                finalMapped: final, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static MappedLibraryInventory CompactInventory(
        ShardedMappedInventoryManifest manifest,
        IReadOnlyList<MappedAllowlistEntry> entries)
    {
        var rows = entries.Select(entry =>
        {
            var relative = Path.GetRelativePath(manifest.SourceRoot, entry.LinkPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var candidate = new LegacyInventoryCandidate(relative, entry.OriginalTarget,
                entry.DavItemId, null, entry.ExclusionReason is null ? "candidate" : "excluded",
                entry.ExclusionReason, null);
            return new MappedLibraryInventoryRow(entry.LinkPath, entry.DavItemId,
                entry.IsBroken, candidate);
        }).ToArray();
        var compact = new MappedLibraryInventory(MappedLibraryInventory.CurrentSchemaVersion,
            manifest.SourceRoot, manifest.LegacyIdsRoot, DateTimeOffset.UtcNow,
            manifest.OutOfScopeFilesystemLinks, MappedLibraryInventory.ComputeRowsSha256(rows), rows);
        compact.Validate();
        return compact;
    }
}
