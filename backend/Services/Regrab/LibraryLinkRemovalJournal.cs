using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services.Regrab;

/// <summary>
/// Append-only JSON-lines audit of every library symlink regrab removes. An <c>intent</c>
/// line is flushed to disk before the unlink and a <c>removed</c> (or <c>failed</c>) line
/// after it, so an interrupted removal can be recognised and audited after a restart.
/// Lives under <c>/config/regrab/</c> with owner-only permissions.
/// </summary>
public sealed class LibraryLinkRemovalJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();

    public LibraryLinkRemovalJournal(string? path = null)
    {
        Path = path ?? System.IO.Path.Join(DavDatabaseContext.ConfigPath, "regrab", "library-link-removals.jsonl");
    }

    public string Path { get; }

    public sealed record Entry
    {
        public required DateTimeOffset Timestamp { get; init; }
        public required string Event { get; init; }
        public required Guid RequestId { get; init; }
        public required string LinkPath { get; init; }
        public string? PreviousTarget { get; init; }
        public Guid? DavItemId { get; init; }
        public string? ReleaseName { get; init; }
        public string? ArrTarget { get; init; }
        public string? Source { get; init; }
        public string? Reason { get; init; }
        public string? Error { get; init; }
    }

    public void Append(Entry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(directory);
                else
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(Path, options);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>True when the journal records an intent or removal of <paramref name="linkPath"/> for this request.</summary>
    public bool HasRemovalFor(Guid requestId, string linkPath) => Read().Any(entry =>
        entry.RequestId == requestId
        && string.Equals(entry.LinkPath, linkPath, StringComparison.Ordinal)
        && entry.Event is "intent" or "removed");

    public IReadOnlyList<Entry> Read()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
                return [];
            var entries = new List<Entry>();
            foreach (var line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(line, JsonOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A torn final line from a crash mid-write is ignored; earlier lines stay valid.
                }
            }

            return entries;
        }
    }
}
