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

public sealed record LegacySourceRecoveryItem(
    string LibraryRelativePath,
    string OriginalTarget,
    Guid LegacyDavItemId,
    string? LegacyPath,
    string Classification,
    string? ExclusionReason,
    string? SourceRelativePath,
    string? PayloadSha256,
    string? IdentityKind,
    string? IdentityDigest,
    long? FileSize);

public sealed record LegacySourceRecoveryReport(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    int TotalLinks,
    int RecoverableLinks,
    decimal RecoverableFraction,
    IReadOnlyList<LegacySourceRecoveryItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record FullRecoveryMasterManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    int TotalLinks,
    int RecoverableLinks,
    decimal RecoverableFraction,
    IReadOnlyList<LegacySourceRecoveryItem> Items)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LegacyRecoveryWriteResult(
    LegacySourceRecoveryReport Report,
    bool MeetsMinimumCoverage);
