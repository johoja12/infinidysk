using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.UsenetMigration.Runner;

public sealed class NzbDavPayloadBuilder(NzbDavPackageReader packageReader) : IMigrationPayloadBuilder
{
    private const string StorePrefix = "nzbdav:";
    public string SourceType => MigrationSourceTypes.NzbDav;

    public async Task<byte[]> BuildAsync(
        MigrationRelease release,
        MigrationSessionState session,
        UsenetMigrationDbContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;
        if (!release.StoreRef.StartsWith(StorePrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"NzbDav release has invalid store reference '{release.StoreRef}'.");
        var root = session.SourcePackageRoot
                   ?? throw new InvalidOperationException("NzbDav submission requires SourcePackageRoot.");
        var package = await packageReader.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        var sourceReleaseId = release.StoreRef[StorePrefix.Length..];
        var exported = package.Manifest.Releases.SingleOrDefault(item =>
                           string.Equals(item.SourceReleaseId, sourceReleaseId, StringComparison.Ordinal))
                       ?? throw new InvalidDataException(
                           $"NzbDav package no longer contains release '{sourceReleaseId}'.");
        var path = package.PayloadPaths[exported.PayloadPath];
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
