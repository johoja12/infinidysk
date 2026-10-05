using System.Text.Json;

namespace NzbWebDAV.Services.Prefetch;

public sealed record SavedPredictionSnapshot(int Version, string Revision, DateTimeOffset UpdatedAt,
    IReadOnlyList<PrefetchPrediction> Predictions);

/// <summary>Persists complete, attributed results only; configuration hashes never contain credentials.</summary>
public sealed class PredictionSnapshotStore(string path)
{
    private const int MaxBytes = 2 * 1024 * 1024;
    public string? Warning { get; private set; }

    public SavedPredictionSnapshot? Load(string revision)
    {
        Warning = null;
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) throw new JsonException();
            var saved = JsonSerializer.Deserialize<SavedPredictionSnapshot>(stream);
            if (saved is null || saved.Version != 1 || saved.Predictions is null || saved.Predictions.Count > 100
                || saved.Predictions.Any(prediction => prediction is null) || saved.UpdatedAt == default)
                throw new JsonException();
            return string.Equals(saved.Revision, revision, StringComparison.Ordinal) ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Warning = "Saved prediction results could not be loaded. A fresh background refresh will be attempted.";
            return null;
        }
    }

    public void Save(SavedPredictionSnapshot snapshot)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            if (bytes.Length > MaxBytes || snapshot.Predictions.Count > 100) throw new IOException();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            Warning = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning = "Prediction results are available, but could not be saved for the next restart.";
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Best effort cleanup. */ }
        }
    }
}
