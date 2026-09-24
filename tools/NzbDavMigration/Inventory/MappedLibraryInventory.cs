using System.Security.Cryptography;
using System.Text.Json;
using NzbDavMigration.Legacy;

namespace NzbDavMigration.Inventory;

public sealed record MappedLibraryInventoryRow(
    string LinkPath,
    Guid DavItemId,
    bool IsBroken,
    LegacyInventoryCandidate Candidate);

public sealed record MappedLibraryInventory(
    int SchemaVersion,
    string SourceRoot,
    string LegacyIdsRoot,
    DateTimeOffset CreatedAt,
    int OutOfScopeFilesystemLinks,
    string RowsSha256,
    IReadOnlyList<MappedLibraryInventoryRow> Rows)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || Rows.Count == 0
            || !Path.IsPathFullyQualified(SourceRoot) || !Path.IsPathFullyQualified(LegacyIdsRoot)
            || OutOfScopeFilesystemLinks < 0)
            throw new InvalidDataException("Mapped inventory schema, roots, or counts are invalid.");
        var source = Path.GetFullPath(SourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var ids = Path.GetFullPath(LegacyIdsRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (source != SourceRoot || ids != LegacyIdsRoot)
            throw new InvalidDataException("Mapped inventory roots are not canonical.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            if (!seen.Add(row.LinkPath) || !row.LinkPath.StartsWith(source + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || Path.GetFullPath(row.LinkPath) != row.LinkPath
                || row.DavItemId == Guid.Empty || row.Candidate.LegacyDavItemId != row.DavItemId
                || !string.Equals(row.Candidate.LibraryRelativePath,
                    Path.GetRelativePath(source, row.LinkPath).Replace(Path.DirectorySeparatorChar, '/'),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Mapped inventory contains a duplicate or inconsistent row.");
            if (row.IsBroken && row.Candidate.ExclusionReason is not ("broken-mapping" or "duplicate-mapped-id"))
                throw new InvalidDataException("Broken mapping was not excluded.");
        }
        if (!string.Equals(RowsSha256, ComputeRowsSha256(Rows), StringComparison.Ordinal))
            throw new InvalidDataException("Mapped inventory digest is invalid.");
    }

    public static string ComputeRowsSha256(IReadOnlyList<MappedLibraryInventoryRow> rows) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rows)))
            .ToLowerInvariant();
}

public sealed class MappedLibraryInventoryBuilder
{
    public MappedLibraryInventory Build(
        string sourceRoot,
        string legacyIdsRoot,
        LegacyMappedReadResult database,
        IReadOnlyList<LibraryInventoryLink> filesystem,
        string blobRoot,
        IReadOnlySet<Guid>? allDuplicateIds = null)
    {
        var root = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        var idsRoot = Path.GetFullPath(legacyIdsRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || !Directory.Exists(idsRoot))
            throw new DirectoryNotFoundException("Source or legacy .ids root is missing.");
        if (new DirectoryInfo(root).LinkTarget is not null || new DirectoryInfo(idsRoot).LinkTarget is not null)
            throw new InvalidDataException("Mapped inventory roots must not be symbolic links.");
        var byPath = filesystem.ToDictionary(link => link.LibraryRelativePath, StringComparer.Ordinal);
        var mappedPaths = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<MappedLibraryInventoryRow>(database.Links.Count);
        var resolver = new LegacyBlobResolver(blobRoot);
        var enrichedByPath = new LibraryInventoryService().Enrich(filesystem, database.Items, resolver)
            .ToDictionary(item => item.LibraryRelativePath, StringComparer.Ordinal);
        var itemsById = database.Items.ToDictionary(item => item.Id);
        var duplicateIds = allDuplicateIds ?? database.Links.GroupBy(link => link.DavItemId)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        foreach (var mapping in database.Links.OrderBy(link => link.LinkPath, StringComparer.Ordinal))
        {
            if (!Path.IsPathFullyQualified(mapping.LinkPath)
                || !mapping.LinkPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFullPath(mapping.LinkPath) != mapping.LinkPath)
                throw new InvalidDataException("Database mapping escaped the selected source root.");
            var relative = Path.GetRelativePath(root, mapping.LinkPath).Replace(Path.DirectorySeparatorChar, '/');
            if (!mappedPaths.Add(relative))
                throw new InvalidDataException("Duplicate mapped LinkPath in source database.");
            byPath.TryGetValue(relative, out var link);
            string? exclusion = null;
            if (duplicateIds.Contains(mapping.DavItemId)) exclusion = "duplicate-mapped-id";
            else if (mapping.IsBroken) exclusion = "broken-mapping";
            else if (link is null) exclusion = "missing-source-link";
            else if (link.LegacyDavItemId != mapping.DavItemId) exclusion = "mapping-target-id-mismatch";
            else if (!IsUnderIdsRoot(mapping.LinkPath, link.OriginalTarget, idsRoot))
                exclusion = "mapping-target-root-mismatch";
            LegacyInventoryCandidate candidate;
            if (exclusion is not null)
                candidate = new LegacyInventoryCandidate(relative, link?.OriginalTarget ?? string.Empty,
                    mapping.DavItemId, itemsById.GetValueOrDefault(mapping.DavItemId), "excluded", exclusion, null);
            else
                candidate = enrichedByPath[relative];
            rows.Add(new MappedLibraryInventoryRow(mapping.LinkPath, mapping.DavItemId,
                mapping.IsBroken, candidate));
        }
        if (rows.Count == 0)
            throw new InvalidDataException("No LocalLinks mappings exist beneath the selected source root.");
        var inventory = new MappedLibraryInventory(MappedLibraryInventory.CurrentSchemaVersion,
            root, idsRoot, DateTimeOffset.UtcNow, byPath.Keys.Count(path => !mappedPaths.Contains(path)),
            MappedLibraryInventory.ComputeRowsSha256(rows), rows);
        inventory.Validate();
        return inventory;
    }

    private static bool IsUnderIdsRoot(string sourcePath, string target, string idsRoot)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var absolute = Path.GetFullPath(Path.IsPathFullyQualified(target)
            ? target : Path.Combine(Path.GetDirectoryName(sourcePath)!, target));
        return absolute.StartsWith(idsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
