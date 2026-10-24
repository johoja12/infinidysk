namespace NzbWebDAV.WebDav;

/// <summary>
/// Durable tombstones for virtual completed links. They never remove media or history.
/// IDs, rather than names, keep replacement files visible. State lives with /config backups.
/// </summary>
public sealed class CompletedSymlinkDeletionStore(string root)
{
    public bool IsDeleted(Guid itemId) => File.Exists(GetPath(itemId));

    public void MarkDeleted(Guid itemId)
    {
        var path = GetPath(itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var marker = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        marker.Flush(flushToDisk: true);
    }

    private string GetPath(Guid itemId)
    {
        if (itemId == Guid.Empty) throw new ArgumentException("An item identity is required.", nameof(itemId));
        var id = itemId.ToString("N");
        return Path.Join(root, id[..2], id);
    }
}
