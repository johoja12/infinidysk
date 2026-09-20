namespace NzbDavMigration.Canary;

public sealed class CanaryApplyJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string PlanPath { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string LibraryRoot { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public List<string> CreatedDirectories { get; set; } = [];
    public List<CanaryApplyJournalLink> Links { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CanaryApplyJournalLink
{
    public string LibraryRelativePath { get; set; } = "";
    public string LinkPath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public long ExpectedFileSize { get; set; }
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
}

public sealed record CanaryRollbackResult(int RemovedLinks, int SkippedLinks, int RemovedDirectories);
