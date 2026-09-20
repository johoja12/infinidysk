namespace NzbWebDAV.UsenetMigration.Runner;

public interface IUsenetMigrationScanRunner
{
    string SourceType { get; }
    Task<ScanSummary?> ScanAsync(CancellationToken cancellationToken = default);
}
