using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbDavMigration.Catalogue;

public sealed class OrphanCatalogueStore : IAsyncDisposable
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _databasePath;
    private readonly string _summaryPath;
    private readonly int _batchSize;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private int _pendingBlobs;
    private bool _complete;

    public OrphanCatalogueStore(string databasePath, string summaryPath, int batchSize = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        _databasePath = Path.GetFullPath(databasePath);
        _summaryPath = Path.GetFullPath(summaryPath);
        _batchSize = batchSize;
    }

    public async Task BeginAsync(string inputDigest, CancellationToken cancellationToken = default)
    {
        ValidateDigest(inputDigest, nameof(inputDigest));
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var connection = RequireConnection();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT InputDigest, Status FROM CatalogueState WHERE Id=1";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existingDigest = reader.GetString(0);
            var status = reader.GetString(1);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!string.Equals(existingDigest, inputDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("The catalogue belongs to a different frozen input list.");
            _complete = string.Equals(status, "complete", StringComparison.Ordinal);
            if (!_complete)
            {
                await DropRedundantMessageIndexAsync(cancellationToken).ConfigureAwait(false);
                await ExecutePragmaAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO CatalogueState(Id,InputDigest,Status,StartedAt,CompletedAt)
            VALUES(1,$digest,'building',$started,NULL)
            """;
        insert.Parameters.AddWithValue("$digest", inputDigest);
        insert.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
    }

    public async Task OpenCompletedAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.Status, "complete", StringComparison.Ordinal))
            throw new InvalidOperationException("Recovery requires a sealed catalogue.");
        // Older sealed catalogues lack this index. Build it once on the writable
        // working copy before recovery starts; otherwise each exact match rereads
        // the entire Articles table to load a single NZB's article sequence.
        await EnsureBlobArticleIndexAsync(cancellationToken).ConfigureAwait(false);
        _complete = true;
    }

    public async Task UpsertAsync(OrphanCatalogueBlob blob, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (_complete) throw new InvalidOperationException("A completed catalogue is immutable.");
        var connection = RequireConnection();
        _transaction ??= (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteTransactionControlAsync("SAVEPOINT blob_upsert", cancellationToken).ConfigureAwait(false);
        try
        {
            // New catalogue entries have no article rows to replace. Probing the
            // Blobs primary key avoids a full Articles scan for every new NZB
            // while the blob-path index is deferred until the catalogue seals.
            bool replacingExisting;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = _transaction;
                existing.CommandText = "SELECT 1 FROM Blobs WHERE RelativePath=$path";
                existing.Parameters.AddWithValue("$path", blob.RelativePath);
                replacingExisting = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
            }

            await using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = _transaction;
                upsert.CommandText = """
                    INSERT INTO Blobs(RelativePath,Length,MtimeTicks,Sha256,ParseStatus,FailureClass,ReleaseDigest,ArticleCount)
                    VALUES($path,$length,$mtime,$sha,$status,$failure,$release,$count)
                    ON CONFLICT(RelativePath) DO UPDATE SET
                      Length=excluded.Length, MtimeTicks=excluded.MtimeTicks, Sha256=excluded.Sha256,
                      ParseStatus=excluded.ParseStatus, FailureClass=excluded.FailureClass,
                      ReleaseDigest=excluded.ReleaseDigest, ArticleCount=excluded.ArticleCount
                    """;
                upsert.Parameters.AddWithValue("$path", blob.RelativePath);
                upsert.Parameters.AddWithValue("$length", blob.Length);
                upsert.Parameters.AddWithValue("$mtime", blob.MtimeTicks);
                upsert.Parameters.AddWithValue("$sha", (object?)blob.Sha256 ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$status", blob.ParseStatus);
                upsert.Parameters.AddWithValue("$failure", (object?)blob.FailureClass ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$release", (object?)blob.ReleaseDigest ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$count", blob.Articles.Count);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (replacingExisting)
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = _transaction;
                delete.CommandText = "DELETE FROM Articles WHERE BlobPath=$path";
                delete.Parameters.AddWithValue("$path", blob.RelativePath);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = _transaction;
                insert.CommandText = """
                    INSERT INTO Articles(MessageId,BlobPath,FileOrdinal,SegmentOrdinal,SegmentBytes)
                    VALUES($message,$path,$file,$segment,$bytes)
                    """;
                var message = insert.Parameters.Add("$message", SqliteType.Text);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var file = insert.Parameters.Add("$file", SqliteType.Integer);
                var segment = insert.Parameters.Add("$segment", SqliteType.Integer);
                var bytes = insert.Parameters.Add("$bytes", SqliteType.Integer);
                path.Value = blob.RelativePath;
                await insert.PrepareAsync(cancellationToken).ConfigureAwait(false);
                foreach (var article in blob.Articles)
                {
                    message.Value = article.MessageId;
                    file.Value = article.FileOrdinal;
                    segment.Value = article.SegmentOrdinal;
                    bytes.Value = article.SegmentBytes;
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await ExecuteTransactionControlAsync("RELEASE SAVEPOINT blob_upsert", cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ExecuteTransactionControlAsync("ROLLBACK TO SAVEPOINT blob_upsert", CancellationToken.None)
                .ConfigureAwait(false);
            await ExecuteTransactionControlAsync("RELEASE SAVEPOINT blob_upsert", CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        _pendingBlobs++;
        if (_pendingBlobs >= _batchSize)
            await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> FindBlobPathsBySha256Async(
        string sha256,
        CancellationToken cancellationToken = default)
    {
        ValidateDigest(sha256, nameof(sha256));
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = "SELECT RelativePath FROM Blobs WHERE Sha256=$sha ORDER BY RelativePath";
        command.Parameters.AddWithValue("$sha", sha256);
        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) paths.Add(reader.GetString(0));
        return paths;
    }

    public async Task<IReadOnlyList<OrphanCatalogueBlob>> FindBlobsContainingAllAsync(
        IEnumerable<string> messageIds,
        CancellationToken cancellationToken = default)
    {
        var ids = messageIds.Select(NormalizeMessageId).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return [];
        // Large media files can exceed SQLite's bound-parameter limit. Probe a
        // deterministic spread, then verify every requested ID in each result.
        const int maxProbeIds = 128;
        var probe = ids.Length <= maxProbeIds
            ? ids
            : Enumerable.Range(0, maxProbeIds)
                .Select(index => ids[(int)((long)index * (ids.Length - 1) / (maxProbeIds - 1))])
                .Distinct(StringComparer.Ordinal).ToArray();
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        var parameters = probe.Select((id, index) =>
        {
            var name = $"$id{index}";
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        command.Parameters.AddWithValue("$count", probe.Length);
        command.CommandText = $"""
            SELECT b.RelativePath
            FROM Blobs b JOIN Articles a ON a.BlobPath=b.RelativePath
            WHERE b.ParseStatus='valid' AND a.MessageId IN ({string.Join(',', parameters)})
            GROUP BY b.RelativePath
            HAVING COUNT(DISTINCT a.MessageId)=$count
            ORDER BY b.RelativePath
            """;
        var paths = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) paths.Add(reader.GetString(0));
        }
        var blobs = new List<OrphanCatalogueBlob>(paths.Count);
        foreach (var path in paths)
        {
            var blob = (await ReadBlobAsync(path, cancellationToken).ConfigureAwait(false))!;
            var available = blob.Articles.Select(article => article.MessageId).ToHashSet(StringComparer.Ordinal);
            if (ids.All(available.Contains)) blobs.Add(blob);
        }
        return blobs;
    }

    public async Task<OrphanCatalogueBlob?> ReadBlobAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT RelativePath,Length,MtimeTicks,Sha256,ParseStatus,FailureClass,ReleaseDigest
            FROM Blobs WHERE RelativePath=$path
            """;
        command.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var blob = new
        {
            Path = reader.GetString(0),
            Length = reader.GetInt64(1),
            Mtime = reader.GetInt64(2),
            Sha = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            Status = reader.GetString(4),
            Failure = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
            Release = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
        };
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var articlesCommand = connection.CreateCommand();
        articlesCommand.Transaction = _transaction;
        articlesCommand.CommandText = """
            SELECT MessageId,FileOrdinal,SegmentOrdinal,SegmentBytes
            FROM Articles WHERE BlobPath=$path ORDER BY FileOrdinal,SegmentOrdinal,MessageId
            """;
        articlesCommand.Parameters.AddWithValue("$path", relativePath);
        var articles = new List<OrphanCatalogueArticle>();
        await using var articlesReader = await articlesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await articlesReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            articles.Add(new OrphanCatalogueArticle(articlesReader.GetString(0), articlesReader.GetInt32(1),
                articlesReader.GetInt32(2), articlesReader.GetInt64(3)));
        return new OrphanCatalogueBlob(blob.Path, blob.Length, blob.Mtime, blob.Sha, blob.Status,
            blob.Failure, blob.Release, articles);
    }

    public async Task<bool> ContainsSnapshotAsync(
        OrphanCatalogueInputItem input,
        CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM Blobs WHERE RelativePath=$path AND Length=$length AND MtimeTicks=$mtime)
            """;
        command.Parameters.AddWithValue("$path", input.RelativePath);
        command.Parameters.AddWithValue("$length", input.Length);
        command.Parameters.AddWithValue("$mtime", input.MtimeTicks);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    public async Task<OrphanCatalogueSummary> BuildSummaryAsync(
        string inputDigest,
        CancellationToken cancellationToken = default)
    {
        ValidateDigest(inputDigest, nameof(inputDigest));
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN ParseStatus='valid' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN ParseStatus='valid' THEN 0 ELSE 1 END),0),
                   (SELECT COUNT(*) FROM Articles)
            FROM Blobs
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new OrphanCatalogueSummary(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), inputDigest);
    }

    public async Task<OrphanCatalogueState> ReadStateAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            SELECT s.InputDigest,s.Status,s.StartedAt,s.CompletedAt,
                   (SELECT COUNT(*) FROM Blobs),(SELECT COUNT(*) FROM Articles)
            FROM CatalogueState s WHERE s.Id=1
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The catalogue has not been started.");
        return new OrphanCatalogueState(
            reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.Parse(reader.GetString(3)),
            reader.GetInt64(4), reader.GetInt64(5));
    }

    public async Task SealAsync(OrphanCatalogueSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ValidateDigest(summary.InputDigest, nameof(summary));
        if (_complete) throw new InvalidOperationException("The catalogue is already complete.");
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(state.InputDigest, summary.InputDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("The summary input digest does not match the catalogue.");
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await EnsureBlobArticleIndexAsync(cancellationToken).ConfigureAwait(false);

        var connection = RequireConnection();
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE CatalogueState SET Status='complete',CompletedAt=$completed WHERE Id=1";
            update.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ExecutePragmaAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync("PRAGMA journal_mode=DELETE;", cancellationToken).ConfigureAwait(false);
        await WriteSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
        _complete = true;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null) return;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
        _pendingBlobs = 0;
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null) return;
        var parent = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidDataException("Catalogue database path has no parent directory.");
        Directory.CreateDirectory(parent);
        if (!File.Exists(_databasePath))
        {
            await using var created = CreatePrivateFile(_databasePath);
            await created.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_databasePath, PrivateFileMode);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync("PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        await using var schema = _connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS CatalogueState(
              Id INTEGER PRIMARY KEY CHECK(Id=1), InputDigest TEXT NOT NULL,
              Status TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT);
            CREATE TABLE IF NOT EXISTS Blobs(
              RelativePath TEXT PRIMARY KEY, Length INTEGER NOT NULL, MtimeTicks INTEGER NOT NULL,
              Sha256 TEXT, ParseStatus TEXT NOT NULL, FailureClass TEXT,
              ReleaseDigest TEXT, ArticleCount INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Articles(
              MessageId TEXT NOT NULL, BlobPath TEXT NOT NULL, FileOrdinal INTEGER NOT NULL,
              SegmentOrdinal INTEGER NOT NULL, SegmentBytes INTEGER NOT NULL,
              PRIMARY KEY(MessageId,BlobPath,FileOrdinal,SegmentOrdinal),
              FOREIGN KEY(BlobPath) REFERENCES Blobs(RelativePath) ON DELETE CASCADE);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DropRedundantMessageIndexAsync(CancellationToken cancellationToken)
    {
        // The Articles primary key already starts with MessageId and serves the
        // same lookups. Older partial catalogues may still carry this extra index.
        await using var command = RequireConnection().CreateCommand();
        command.CommandText = "DROP INDEX IF EXISTS IX_Articles_MessageId;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecutePragmaAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = RequireConnection().CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureBlobArticleIndexAsync(CancellationToken cancellationToken)
    {
        await using var command = RequireConnection().CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Articles_BlobPath_Order
            ON Articles(BlobPath,FileOrdinal,SegmentOrdinal,MessageId)
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteTransactionControlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = RequireConnection().CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteSummaryAsync(OrphanCatalogueSummary summary, CancellationToken cancellationToken)
    {
        if (File.Exists(_summaryPath) || Directory.Exists(_summaryPath))
            throw new IOException($"Catalogue summary destination already exists: {_summaryPath}");
        var parent = Path.GetDirectoryName(_summaryPath)
            ?? throw new InvalidDataException("Catalogue summary path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Join(parent, $".{Path.GetFileName(_summaryPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            var document = new OrphanCatalogueCompletionDocument(
                summary, OrphanCatalogueCompletionDocument.ComputeDigest(summary));
            await using (var output = CreatePrivateFile(temporary))
            {
                var bytes = Encoding.UTF8.GetBytes(document.Serialize());
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Completion marker must be durable before atomic rename.
                output.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }
            File.Move(temporary, _summaryPath);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_summaryPath, PrivateFileMode);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        return new FileStream(path, options);
    }

    private SqliteConnection RequireConnection() =>
        _connection ?? throw new InvalidOperationException("BeginAsync must be called before using the catalogue.");

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Expected a 64-character SHA-256 digest.", parameterName);
    }

    private static string NormalizeMessageId(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith('<') && normalized.EndsWith('>') && normalized.Length >= 2)
            normalized = normalized[1..^1].Trim();
        if (normalized.Length == 0) throw new InvalidDataException("Article identifier is empty.");
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.CommitAsync().ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }
}
