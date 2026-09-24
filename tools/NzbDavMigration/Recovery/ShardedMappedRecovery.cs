using System.Security.Cryptography;
using System.Text.Json;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;

namespace NzbDavMigration.Recovery;

public sealed record MappedRecoveryShard(
    int Index,
    string RelativePath,
    string InventoryRowsSha256,
    int TotalLinks,
    int RecoverableLinks,
    string MasterSha256);

public sealed record ShardedMappedRecoveryManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string SourceRoot,
    string LegacyIdsRoot,
    string InventoryRowsSha256,
    string CatalogueInputSha256,
    int TotalLinks,
    int RecoverableLinks,
    decimal RecoverableFraction,
    IReadOnlyList<MappedRecoveryShard> Shards)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate(ShardedMappedInventoryManifest inventory)
    {
        if (SchemaVersion != CurrentSchemaVersion || SourceRoot != inventory.SourceRoot
            || LegacyIdsRoot != inventory.LegacyIdsRoot || InventoryRowsSha256 != inventory.RowsSha256
            || CatalogueInputSha256.Length != 64
            || CatalogueInputSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || Shards.Count != inventory.Shards.Count || TotalLinks != inventory.RowCount
            || RecoverableLinks < 0 || RecoverableLinks > TotalLinks
            || RecoverableFraction != decimal.Divide(RecoverableLinks, TotalLinks))
            throw new InvalidDataException("Sharded recovery header or coverage disagrees with mapped inventory.");
        var total = 0;
        var recovered = 0;
        for (var index = 0; index < Shards.Count; index++)
        {
            var shard = Shards[index];
            if (shard.Index != index || shard.RelativePath != $"shards/{index + 1:D6}/master-manifest.json"
                || shard.InventoryRowsSha256 != inventory.Shards[index].RowsSha256
                || shard.TotalLinks != inventory.Shards[index].RowCount
                || shard.RecoverableLinks < 0 || shard.RecoverableLinks > shard.TotalLinks
                || shard.MasterSha256.Length != 64
                || shard.MasterSha256.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Sharded recovery entry disagrees with mapped inventory.");
            total = checked(total + shard.TotalLinks);
            recovered = checked(recovered + shard.RecoverableLinks);
        }
        if (total != TotalLinks || recovered != RecoverableLinks)
            throw new InvalidDataException("Sharded recovery totals disagree with its entries.");
    }
}

public sealed class ShardedMappedRecovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ShardedMappedRecoveryManifest> WriteAsync(
        string inventoryDirectory,
        OrphanCatalogueStore catalogue,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var inventory = await ShardedMappedInventoryWriter.ReadManifestAsync(
            inventoryDirectory, cancellationToken).ConfigureAwait(false);
        var catalogueState = await catalogue.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (catalogueState.Status != "complete")
            throw new InvalidDataException("Sharded recovery requires a sealed NZB catalogue.");
        var catalogueDigest = catalogueState.InputDigest;
        var output = Path.GetFullPath(outputDirectory);
        if (File.Exists(output) || File.Exists(Path.Join(output, "manifest.json")))
            throw new IOException("A completed sharded recovery already exists at the destination.");
        if (Directory.Exists(output) && new DirectoryInfo(output).LinkTarget is not null)
            throw new InvalidDataException("Recovery output cannot be a symbolic link.");
        Directory.CreateDirectory(Path.Join(output, "shards"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Join(output, "shards"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var shards = new List<MappedRecoveryShard>(inventory.Shards.Count);
        foreach (var entry in inventory.Shards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await ShardedMappedInventoryWriter.ReadVerifiedShardAsync(
                inventoryDirectory, inventory, entry, cancellationToken).ConfigureAwait(false);
            var shardDirectory = Path.Join(output, "shards", $"{entry.Index + 1:D6}");
            if (!Directory.Exists(shardDirectory))
            {
                var shardStage = shardDirectory + $".tmp-{Guid.NewGuid():N}";
                await new LegacySourceRecovery().WriteAsync(
                    source.Rows.Select(row => row.Candidate).ToArray(), catalogue, shardStage, 0m,
                    new MappedSourceProof(source.SourceRoot, source.LegacyIdsRoot,
                        source.Rows.Count, source.RowsSha256), cancellationToken).ConfigureAwait(false);
                try
                {
                    await using (var provenance = CanaryPackageWriter.CreatePrivateFile(
                                     Path.Join(shardStage, "catalogue-input.sha256")))
                    {
                        await provenance.WriteAsync(System.Text.Encoding.ASCII.GetBytes(catalogueDigest + "\n"),
                            cancellationToken).ConfigureAwait(false);
                        await provenance.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Publish only durable catalogue provenance.
                        provenance.Flush(flushToDisk: true);
#pragma warning restore CA1849
                    }
                    Directory.Move(shardStage, shardDirectory);
                }
                finally { if (Directory.Exists(shardStage)) Directory.Delete(shardStage, recursive: true); }
            }
            if (await File.ReadAllTextAsync(Path.Join(shardDirectory, "catalogue-input.sha256"),
                    cancellationToken).ConfigureAwait(false) != catalogueDigest + "\n")
                throw new InvalidDataException("Recovery shard came from a different NZB catalogue.");
            var masterPath = Path.Join(shardDirectory, "master-manifest.json");
            var master = await ReadMasterAsync(masterPath, cancellationToken).ConfigureAwait(false);
            ValidateMaster(master, source);
            shards.Add(new MappedRecoveryShard(entry.Index,
                $"shards/{entry.Index + 1:D6}/master-manifest.json", entry.RowsSha256,
                master.TotalLinks, master.RecoverableLinks,
                await Sha256Async(masterPath, cancellationToken).ConfigureAwait(false)));
        }
        var recovered = shards.Sum(shard => shard.RecoverableLinks);
        var manifest = new ShardedMappedRecoveryManifest(
            ShardedMappedRecoveryManifest.CurrentSchemaVersion, DateTimeOffset.UtcNow,
            inventory.SourceRoot, inventory.LegacyIdsRoot, inventory.RowsSha256, catalogueDigest,
            inventory.RowCount, recovered, decimal.Divide(recovered, inventory.RowCount), shards);
        manifest.Validate(inventory);
        var stage = Path.Join(output, $".manifest.tmp-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = CanaryPackageWriter.CreatePrivateFile(stage))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Publish only a durable recovery manifest.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(stage, Path.Join(output, "manifest.json"));
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
        return manifest;
    }

    public static async Task<ShardedMappedRecoveryManifest> ReadManifestAsync(
        string directory,
        ShardedMappedInventoryManifest inventory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        if (new DirectoryInfo(root).LinkTarget is not null
            || new FileInfo(Path.Join(root, "manifest.json")).LinkTarget is not null)
            throw new InvalidDataException("Recovery artifact cannot be a symbolic link.");
        await using var stream = File.OpenRead(Path.Join(root, "manifest.json"));
        var manifest = await JsonSerializer.DeserializeAsync<ShardedMappedRecoveryManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Sharded recovery manifest is empty.");
        manifest.Validate(inventory);
        return manifest;
    }

    public static async Task<FullRecoveryMasterManifest> ReadVerifiedShardAsync(
        string directory,
        ShardedMappedRecoveryManifest recovery,
        MappedRecoveryShard shard,
        MappedLibraryInventory inventory,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        var shardDirectory = Path.GetDirectoryName(Path.Join(root, shard.RelativePath))!;
        if (new DirectoryInfo(root).LinkTarget is not null
            || new DirectoryInfo(Path.Join(root, "shards")).LinkTarget is not null
            || new DirectoryInfo(shardDirectory).LinkTarget is not null)
            throw new InvalidDataException("Recovery shard directory cannot be a symbolic link.");
        var path = Path.Join(root, shard.RelativePath);
        if (await File.ReadAllTextAsync(Path.Join(Path.GetDirectoryName(path)!, "catalogue-input.sha256"),
                cancellationToken).ConfigureAwait(false) != recovery.CatalogueInputSha256 + "\n")
            throw new InvalidDataException("Recovery shard catalogue provenance changed.");
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Recovery shard cannot be a symbolic link.");
        if (await Sha256Async(path, cancellationToken).ConfigureAwait(false) != shard.MasterSha256)
            throw new InvalidDataException("Recovery shard file digest changed.");
        var master = await ReadMasterAsync(path, cancellationToken).ConfigureAwait(false);
        ValidateMaster(master, inventory);
        if (master.TotalLinks != shard.TotalLinks || master.RecoverableLinks != shard.RecoverableLinks
            || master.MappedSource?.RowsSha256 != shard.InventoryRowsSha256
            || recovery.InventoryRowsSha256.Length != 64)
            throw new InvalidDataException("Recovery shard disagrees with its sealed manifest.");
        return master;
    }

    private static async Task<FullRecoveryMasterManifest> ReadMasterAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<FullRecoveryMasterManifest>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Recovery shard master is empty.");
    }

    private static void ValidateMaster(FullRecoveryMasterManifest master, MappedLibraryInventory inventory)
    {
        if (master.SchemaVersion != FullRecoveryMasterManifest.CurrentSchemaVersion
            || master.MappedSource?.SourceRoot != inventory.SourceRoot
            || master.MappedSource.LegacyIdsRoot != inventory.LegacyIdsRoot
            || master.MappedSource.RowCount != inventory.Rows.Count
            || master.MappedSource.RowsSha256 != inventory.RowsSha256
            || master.TotalLinks != inventory.Rows.Count || master.Items.Count != inventory.Rows.Count
            || master.RecoverableLinks != master.Items.Count(item => item.Classification is "exact-direct" or "exact-archive")
            || master.RecoverableFraction != decimal.Divide(master.RecoverableLinks, master.TotalLinks))
            throw new InvalidDataException("Recovery shard counts or source proof are invalid.");
        var byPath = inventory.Rows.ToDictionary(row => row.Candidate.LibraryRelativePath, StringComparer.Ordinal);
        foreach (var item in master.Items)
        {
            if (!byPath.Remove(item.LibraryRelativePath, out var mapped)
                || mapped.DavItemId != item.LegacyDavItemId
                || mapped.Candidate.OriginalTarget != item.OriginalTarget
                || (item.Classification is "exact-direct" or "exact-archive"
                    && (mapped.IsBroken || mapped.Candidate.ExclusionReason is not null)))
                throw new InvalidDataException("Recovery shard contains a file outside its mapped allowlist.");
        }
        if (byPath.Count != 0)
            throw new InvalidDataException("Recovery shard omitted mapped files.");
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false)).ToLowerInvariant();
    }
}
