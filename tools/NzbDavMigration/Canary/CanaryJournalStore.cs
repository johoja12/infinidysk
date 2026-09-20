using System.Text.Json;

namespace NzbDavMigration.Canary;

internal static class CanaryJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<CanaryApplyJournal?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<CanaryApplyJournal>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException("Canary apply journal is empty.");
    }

    public static async Task WriteAsync(
        string path,
        CanaryApplyJournal journal,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(parent);
        journal.UpdatedAt = DateTimeOffset.UtcNow;
        var temporary = Path.Join(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
