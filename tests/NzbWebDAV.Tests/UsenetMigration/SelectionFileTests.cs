using System.Text.Json;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class SelectionFileTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"selection-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAndValidateAsync_RequiresExactReviewedInventoryPairs()
    {
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid();
        var inventory = new[] { new LibraryInventoryLink("TV/a.mkv", "/legacy/a", id) };
        var valid = Path.Join(_root, "valid.json");
        await File.WriteAllTextAsync(valid, JsonSerializer.Serialize(
            new SelectionDocument([new SelectionEntry("TV/a.mkv", id)])));

        var result = await SelectionFile.LoadAndValidateAsync(valid, inventory, 1, 1);
        Assert.Single(result.Items);

        var invalid = Path.Join(_root, "invalid.json");
        await File.WriteAllTextAsync(invalid, JsonSerializer.Serialize(
            new SelectionDocument([new SelectionEntry("TV/a.mkv", Guid.NewGuid())])));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SelectionFile.LoadAndValidateAsync(invalid, inventory, 1, 1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
