namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed class MigrationCorrelationDispatcher
{
    private readonly Dictionary<string, IMigrationCorrelationProvider> _providers;

    public MigrationCorrelationDispatcher(IEnumerable<IMigrationCorrelationProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.SourceType, StringComparer.Ordinal);
    }

    public IMigrationCorrelationProvider Resolve(string sourceType) =>
        _providers.TryGetValue(sourceType, out var provider)
            ? provider
            : throw new InvalidOperationException($"Unsupported migration correlation source '{sourceType}'.");
}
