namespace NzbWebDAV.UsenetMigration.Canary;

public sealed record NzbDavCanaryPlan(
    int SchemaVersion,
    long RunId,
    string SourcePackageDigest,
    DateTimeOffset CreatedAt,
    int SelectedCount,
    int ActionableCount,
    bool IsValid,
    IReadOnlyList<NzbDavCanaryPlanLink> Links)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record NzbDavCanaryPlanLink(
    string LibraryRelativePath,
    string OriginalLegacyTarget,
    Guid LegacyDavItemId,
    long ExpectedFileSize,
    string CorrelationStatus,
    string CorrelationEvidence,
    string? NewRelativeTarget,
    string ApplyStatus);

public sealed record NzbDavCanaryPlanResult(NzbDavCanaryPlan Plan, string PlanDirectory);
