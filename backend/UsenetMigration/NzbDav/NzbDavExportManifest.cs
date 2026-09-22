using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NzbWebDAV.UsenetMigration.Naming;

namespace NzbWebDAV.UsenetMigration.NzbDav;

public sealed record NzbDavExportManifest(
    int SchemaVersion,
    string PackageId,
    DateTimeOffset CreatedAt,
    string Source,
    IReadOnlyList<NzbDavExportRelease> Releases,
    IReadOnlyList<NzbDavSelectedLibraryLink> SelectedLinks,
    IReadOnlyList<NzbDavPayloadFile> Payloads,
    IReadOnlyList<NzbDavChecksumEntry> Checksums,
    string? MasterManifestDigest = null,
    int? BatchIndex = null,
    int? BatchCount = null)
{
    public const int CurrentSchemaVersion = 2;

    public void Validate()
    {
        if (SchemaVersion is not (1 or CurrentSchemaVersion))
            throw new InvalidDataException($"Unsupported NzbDav export schema version {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(PackageId))
            throw new InvalidDataException("NzbDav export package id is required.");
        if (!string.Equals(Source, MigrationSourceTypes.NzbDav, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported migration source '{Source}'.");
        if (SchemaVersion == 2)
        {
            RequireSha256(MasterManifestDigest ?? string.Empty, "master manifest");
            if (BatchIndex is null or < 0 || BatchCount is null or <= 0 || BatchIndex >= BatchCount)
                throw new InvalidDataException("Schema-v2 packages require valid batch index and count values.");
        }

        foreach (var release in Releases)
        {
            RequirePackagePath(release.PayloadPath, "release payload");
            if ((release.SourceFileName is null) != (release.SourceJobName is null))
                throw new InvalidDataException("NzbDav source filename and job name must be supplied together.");
            if (release.SourceFileName is not null)
            {
                if (string.IsNullOrWhiteSpace(release.SourceFileName)
                    || release.SourceFileName is "." or ".."
                    || release.SourceFileName.IndexOfAny(['/', '\\']) >= 0)
                    throw new InvalidDataException($"Unsafe NzbDav source filename '{release.SourceFileName}'.");
                if (string.IsNullOrWhiteSpace(release.SourceJobName))
                    throw new InvalidDataException("NzbDav source job name is required.");
                var expectedJobName = NzbDavNaming.JobName(release.SourceFileName);
                if (!string.Equals(expectedJobName, release.SourceJobName, StringComparison.Ordinal))
                    throw new InvalidDataException("NzbDav source filename and job name disagree.");
            }
            foreach (var leaf in release.Leaves)
            {
                if (leaf.LegacyDavItemId == Guid.Empty)
                    throw new InvalidDataException("A source leaf has an empty legacy DavItem id.");
                if (leaf.FileSize < 0)
                    throw new InvalidDataException($"Source leaf '{leaf.LegacyDavItemId}' has a negative size.");
                if (SchemaVersion == 2
                    && string.Equals(leaf.IdentityKind, NzbDavStableArchiveIdentity.Kind, StringComparison.Ordinal))
                {
                    if (!string.Equals(leaf.StableIdentityKind, NzbDavStableArchiveIdentity.Kind,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("Schema-v2 archive leaves require a stable identity kind.");
                    RequireSha256(leaf.StableIdentityDigest ?? string.Empty, $"leaf {leaf.LegacyDavItemId}");
                    if (!string.Equals(leaf.StableIdentityKind, leaf.IdentityKind, StringComparison.Ordinal)
                        || !string.Equals(leaf.StableIdentityDigest, leaf.IdentityDigest, StringComparison.Ordinal))
                        throw new InvalidDataException("Schema-v2 stable archive identity disagrees with correlation identity.");
                }
            }
        }

        foreach (var link in SelectedLinks)
            RequirePackagePath(link.LibraryRelativePath, "library-relative path");
        foreach (var payload in Payloads)
        {
            RequirePackagePath(payload.RelativePath, "payload");
            RequireSha256(payload.Sha256, payload.RelativePath);
            if (payload.Length < 0)
                throw new InvalidDataException($"Payload '{payload.RelativePath}' has a negative length.");
        }
        foreach (var checksum in Checksums)
        {
            RequirePackagePath(checksum.RelativePath, "checksum");
            RequireSha256(checksum.Sha256, checksum.RelativePath);
        }
    }

    internal static string RequirePackagePath(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith('/')
            || value.StartsWith('\\')
            || value.Any(character => character == '\\')
            || Regex.IsMatch(value, "^[A-Za-z]:"))
        {
            throw new InvalidDataException($"Unsafe {field} '{value}'.");
        }

        var components = value.Split('/');
        if (components.Any(component => component is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe {field} '{value}'.");
        return string.Join('/', components);
    }

    private static void RequireSha256(string value, string path)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"Entry '{path}' has an invalid SHA-256 digest.");
        }
    }
}

public sealed record NzbDavExportRelease(
    string SourceReleaseId,
    Guid? NzbBlobId,
    string PayloadPath,
    IReadOnlyList<NzbDavExportLeaf> Leaves,
    string? SourceFileName = null,
    string? SourceJobName = null);

public sealed record NzbDavExportLeaf(
    Guid LegacyDavItemId,
    string LegacyPath,
    long FileSize,
    string ParentReleaseId,
    Guid? HistoryId,
    Guid? NzbBlobId,
    string IdentityKind,
    string? IdentityDigest,
    string ExtractionStatus,
    string? ExclusionReason,
    string? StableIdentityKind = null,
    string? StableIdentityDigest = null);

public sealed record NzbDavSelectedLibraryLink(
    string LibraryRelativePath,
    string OriginalTarget,
    Guid LegacyDavItemId);

public sealed record NzbDavPayloadFile(string RelativePath, long Length, string Sha256);

public sealed record NzbDavChecksumEntry(string RelativePath, string Sha256);

public static class NzbDavExportManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(NzbDavExportManifest manifest)
    {
        manifest.Validate();
        return JsonSerializer.Serialize(manifest, Options) + "\n";
    }

    public static NzbDavExportManifest Deserialize(string json)
    {
        NzbDavExportManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<NzbDavExportManifest>(json, Options)
                       ?? throw new InvalidDataException("NzbDav export manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("NzbDav export manifest is invalid JSON.", exception);
        }

        manifest.Validate();
        return manifest;
    }
}
