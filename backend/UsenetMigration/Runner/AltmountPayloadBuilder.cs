using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;

namespace NzbWebDAV.UsenetMigration.Runner;

public sealed class AltmountPayloadBuilder : IMigrationPayloadBuilder
{
    public string SourceType => MigrationSourceTypes.Altmount;

    public Task<byte[]> BuildAsync(
        MigrationRelease release,
        MigrationSessionState session,
        UsenetMigrationDbContext context,
        CancellationToken cancellationToken = default) =>
        SubmissionWorkerPool.BuildAltmountNzbAsync(release, session, context, cancellationToken);
}
