namespace NzbWebDAV.UsenetMigration.Runner;

public sealed class MigrationScanDispatcher
{
    private readonly Dictionary<string, IUsenetMigrationScanRunner> _scanners;

    public MigrationScanDispatcher(IEnumerable<IUsenetMigrationScanRunner> scanners)
    {
        ArgumentNullException.ThrowIfNull(scanners);
        try
        {
            _scanners = scanners.ToDictionary(scanner => scanner.SourceType, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Migration scan source registrations must be unique.", exception);
        }
    }

    public IUsenetMigrationScanRunner Resolve(string sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || !_scanners.TryGetValue(sourceType, out var scanner))
            throw new InvalidOperationException($"Unsupported migration source '{sourceType}'.");
        return scanner;
    }
}
