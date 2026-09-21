namespace NzbWebDAV.UsenetMigration.Model;

public sealed record NzbDavBatchStatus(
    long Id,
    int BatchIndex,
    string PackageDigest,
    int SelectionCount,
    string Status,
    long? RunId,
    string? PlanDigest,
    int AppliedCount,
    int ValidatedCount);

public sealed record NzbDavMasterStatus(
    long Id,
    string ManifestDigest,
    int SourceLinkCount,
    int RecoverableCount,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<NzbDavBatchStatus> Batches);
