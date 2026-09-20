namespace NzbWebDAV.UsenetMigration.Runner;

public sealed class MigrationPayloadBuilderDispatcher
{
    private readonly Dictionary<string, IMigrationPayloadBuilder> _builders;

    public MigrationPayloadBuilderDispatcher(IEnumerable<IMigrationPayloadBuilder> builders)
    {
        ArgumentNullException.ThrowIfNull(builders);
        try
        {
            _builders = builders.ToDictionary(builder => builder.SourceType, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Migration payload builder registrations must be unique.", exception);
        }
    }

    public IMigrationPayloadBuilder Resolve(string sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || !_builders.TryGetValue(sourceType, out var builder))
            throw new InvalidOperationException($"Unsupported migration payload source '{sourceType}'.");
        return builder;
    }
}
