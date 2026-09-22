using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbDavMigration.Export;

public sealed record CanaryExportRelease(
    string SourceReleaseId,
    Guid? NzbBlobId,
    string PayloadSourcePath,
    IReadOnlyList<NzbDavExportLeaf> Leaves,
    byte[]? PayloadBytes = null,
    string? SourceFileName = null,
    string? SourceJobName = null);

public sealed record CanaryExportRequest(
    string PackageId,
    string OutputDirectory,
    IReadOnlyList<CanaryExportRelease> Releases,
    IReadOnlyList<NzbDavSelectedLibraryLink> SelectedLinks);

public sealed partial class CanaryPackageWriter(int minimumLinks = 20, int maximumLinks = 50)
{
    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeReleaseIdRegex();

    public async Task WriteAsync(CanaryExportRequest request, CancellationToken cancellationToken = default)
        => await WriteCoreAsync(request, schemaVersion: 1, masterManifestDigest: null,
            batchIndex: null, batchCount: null, enforceCanaryBounds: true, cancellationToken).ConfigureAwait(false);

    public async Task WriteFullBatchAsync(
        CanaryExportRequest request,
        string masterManifestDigest,
        int batchIndex,
        int batchCount,
        CancellationToken cancellationToken = default)
        => await WriteCoreAsync(request, NzbDavExportManifest.CurrentSchemaVersion, masterManifestDigest,
            batchIndex, batchCount, enforceCanaryBounds: false, cancellationToken).ConfigureAwait(false);

    private async Task WriteCoreAsync(
        CanaryExportRequest request,
        int schemaVersion,
        string? masterManifestDigest,
        int? batchIndex,
        int? batchCount,
        bool enforceCanaryBounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Directory.Exists(request.OutputDirectory) || File.Exists(request.OutputDirectory))
            throw new IOException($"Export destination already exists: {request.OutputDirectory}");
        if (enforceCanaryBounds
            && (request.SelectedLinks.Count < minimumLinks || request.SelectedLinks.Count > maximumLinks))
            throw new InvalidDataException($"Selection must contain between {minimumLinks} and {maximumLinks} links.");
        RequireUnique(request.SelectedLinks.Select(link => link.LibraryRelativePath), "library path");
        RequireUnique(request.SelectedLinks.Select(link => link.LegacyDavItemId.ToString()), "legacy DavItem ID");

        var leafIds = request.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal))
            .Select(leaf => leaf.LegacyDavItemId)
            .ToHashSet();
        if (request.SelectedLinks.Any(link => !leafIds.Contains(link.LegacyDavItemId)))
            throw new InvalidDataException("Every selected link must resolve to a ready export leaf.");

        var output = Path.GetFullPath(request.OutputDirectory);
        var parent = Path.GetDirectoryName(output) ?? throw new InvalidDataException("Export destination has no parent.");
        Directory.CreateDirectory(parent);
        var stage = Path.Join(parent, $".{Path.GetFileName(output)}.tmp-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stage);
        else Directory.CreateDirectory(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var committed = false;
        try
        {
            var payloads = new List<NzbDavPayloadFile>();
            var checksums = new List<NzbDavChecksumEntry>();
            var manifestReleases = new List<NzbDavExportRelease>();
            Directory.CreateDirectory(Path.Join(stage, "payloads"));
            foreach (var release in request.Releases.OrderBy(item => item.SourceReleaseId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SafeReleaseIdRegex().IsMatch(release.SourceReleaseId))
                    throw new InvalidDataException($"Unsafe source release id '{release.SourceReleaseId}'.");
                var relativePath = $"payloads/{release.SourceReleaseId}.nzb";
                var destination = Path.Join(stage, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (release.PayloadBytes is { } bytes)
                    await WriteBytesAndFlushAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
                else
                    await CopyAndFlushAsync(release.PayloadSourcePath, destination, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(destination);
                var digest = await ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
                payloads.Add(new NzbDavPayloadFile(relativePath, info.Length, digest));
                checksums.Add(new NzbDavChecksumEntry(relativePath, digest));
                manifestReleases.Add(new NzbDavExportRelease(
                    release.SourceReleaseId, release.NzbBlobId, relativePath, release.Leaves,
                    release.SourceFileName, release.SourceJobName));
            }

            var manifest = new NzbDavExportManifest(
                schemaVersion,
                request.PackageId,
                DateTimeOffset.UtcNow,
                MigrationSourceTypes.NzbDav,
                manifestReleases,
                request.SelectedLinks.OrderBy(link => link.LibraryRelativePath, StringComparer.Ordinal).ToArray(),
                payloads,
                checksums,
                masterManifestDigest,
                batchIndex,
                batchCount);
            var manifestPath = Path.Join(stage, "manifest.json");
            await WriteAndFlushAsync(manifestPath, NzbDavExportManifestJson.Serialize(manifest), cancellationToken)
                .ConfigureAwait(false);

            await WriteReportsAsync(stage, manifest, cancellationToken).ConfigureAwait(false);
            var checksumFiles = payloads.Select(payload => payload.RelativePath)
                .Concat(["manifest.json", "success.json", "exclusions.json"])
                .Order(StringComparer.Ordinal);
            var lines = new StringBuilder();
            foreach (var relativePath in checksumFiles)
            {
                var path = Path.Join(stage, relativePath.Replace('/', Path.DirectorySeparatorChar));
                lines.Append(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false))
                    .Append("  ").Append(relativePath).Append('\n');
            }
            await WriteAndFlushAsync(Path.Join(stage, "SHA256SUMS"), lines.ToString(), cancellationToken)
                .ConfigureAwait(false);

            Directory.Move(stage, output);
            committed = true;
        }
        finally
        {
            if (!committed && Directory.Exists(stage))
                Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task WriteReportsAsync(
        string stage,
        NzbDavExportManifest manifest,
        CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var success = manifest.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal));
        var exclusions = manifest.Releases.SelectMany(release => release.Leaves)
            .Where(leaf => !string.Equals(leaf.ExtractionStatus, "ready", StringComparison.Ordinal));
        await WriteAndFlushAsync(Path.Join(stage, "success.json"),
            JsonSerializer.Serialize(success, options) + "\n", cancellationToken).ConfigureAwait(false);
        await WriteAndFlushAsync(Path.Join(stage, "exclusions.json"),
            JsonSerializer.Serialize(exclusions, options) + "\n", cancellationToken).ConfigureAwait(false);
    }

    private static void RequireUnique(IEnumerable<string> values, string field)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            throw new InvalidDataException($"Selection contains a duplicate {field}.");
    }

    private static async Task CopyAndFlushAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (!info.Exists)
            throw new FileNotFoundException("Export payload is missing.", source);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Export payload must not be a symbolic link.");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = CreatePrivateFile(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Atomic export durability requires fsync before rename.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static async Task WriteAndFlushAsync(string path, string content, CancellationToken cancellationToken)
        => await WriteBytesAndFlushAsync(path, Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);

    private static async Task WriteBytesAndFlushAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var output = CreatePrivateFile(path);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Atomic export durability requires fsync before rename.
        output.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 16 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
