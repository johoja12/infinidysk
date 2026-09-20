using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;

namespace NzbWebDAV.UsenetMigration.Provenance;

public sealed record ImportedArticleIdentity(
    Guid DavItemId,
    string Name,
    string Path,
    long? FileSize,
    string? IdentityKind,
    string? IdentityDigest,
    Guid? NzbBlobId);

public sealed record MigrationCorrelationResult(
    long ReleaseFileId,
    string Status,
    Guid? DavItemId,
    Guid? NzbBlobId,
    string? MatchMethod,
    string Evidence);

public interface IMigrationCorrelationProvider
{
    string SourceType { get; }
    Task<IReadOnlyList<MigrationCorrelationResult>> CorrelateAsync(
        IReadOnlyList<MigrationReleaseFile> sourceFiles,
        IReadOnlyList<DavItem> importedLeaves,
        DavDatabaseContext davContext,
        CancellationToken cancellationToken = default);
}
