using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;

namespace NzbWebDAV.UsenetMigration.Runner;

public interface IMigrationPayloadBuilder
{
    string SourceType { get; }
    Task<byte[]> BuildAsync(
        MigrationRelease release,
        MigrationSessionState session,
        UsenetMigrationDbContext context,
        CancellationToken cancellationToken = default);
}
