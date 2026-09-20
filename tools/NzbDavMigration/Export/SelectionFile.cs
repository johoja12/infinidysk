using System.Text.Json;
using NzbDavMigration.Inventory;

namespace NzbDavMigration.Export;

public sealed record SelectionEntry(string LibraryRelativePath, Guid LegacyDavItemId);
public sealed record SelectionDocument(IReadOnlyList<SelectionEntry> Items);

public static class SelectionFile
{
    public static async Task<SelectionDocument> LoadAndValidateAsync(
        string path,
        IReadOnlyCollection<LibraryInventoryLink> inventory,
        int minimum,
        int maximum,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SelectionDocument>(stream,
                           cancellationToken: cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("Selection file is empty.");
        if (document.Items.Count < minimum || document.Items.Count > maximum)
            throw new InvalidDataException($"Selection must contain between {minimum} and {maximum} links.");
        if (document.Items.Select(item => item.LibraryRelativePath).Distinct(StringComparer.Ordinal).Count()
            != document.Items.Count)
            throw new InvalidDataException("Selection contains duplicate library paths.");
        if (document.Items.Select(item => item.LegacyDavItemId).Distinct().Count() != document.Items.Count)
            throw new InvalidDataException("Selection contains duplicate legacy DavItem IDs.");

        var indexed = inventory.ToDictionary(
            item => (item.LibraryRelativePath, item.LegacyDavItemId));
        if (document.Items.Any(item => !indexed.ContainsKey((item.LibraryRelativePath, item.LegacyDavItemId))))
            throw new InvalidDataException("Selection contains a path or legacy ID that is absent from inventory.");
        return document;
    }
}
