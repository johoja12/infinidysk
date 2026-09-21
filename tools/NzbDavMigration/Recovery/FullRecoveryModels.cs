namespace NzbDavMigration.Recovery;

public sealed record FullRecoverySnapshotDigests(
    string LibraryLinksSha256,
    string LegacyRowsSha256);

public sealed record FullRecoveryInventoryItem(
    string LibraryRelativePath,
    Guid LegacyDavItemId,
    string TerminalClassification,
    string? ExclusionReason);

public sealed record FullRecoveryInventory(
    int SchemaVersion,
    FullRecoverySnapshotDigests SourceSnapshot,
    DateTimeOffset CreatedAt,
    IReadOnlyList<FullRecoveryInventoryItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}
