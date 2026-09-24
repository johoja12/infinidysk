using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace NzbDavMigration.Legacy;

public sealed record LegacyDavItemRow(
    Guid Id,
    string Path,
    long? FileSize,
    int Type,
    Guid? HistoryItemId,
    Guid? NzbBlobId,
    string? HistoryFileName = null,
    string? HistoryJobName = null,
    string? HistoryCategory = null,
    int? HistoryDownloadStatus = null,
    string? NzbSegmentsJson = null,
    string? RarPartsJson = null,
    string? MultipartMetadataJson = null,
    string? NzbContents = null,
    string? ReleaseRootPath = null,
    string? ResolutionExclusion = null,
    string? HistoryExclusion = null,
    string? SafetyExclusion = null);

public sealed record LegacyReadResult(
    IReadOnlyList<LegacyDavItemRow> Items,
    IReadOnlyList<Guid> MissingIds);

public sealed record LegacyLocalLinkRow(string LinkPath, Guid DavItemId, bool IsBroken);

public sealed record LegacyMappedReadResult(
    IReadOnlyList<LegacyLocalLinkRow> Links,
    IReadOnlyList<LegacyDavItemRow> Items,
    IReadOnlyList<Guid> MissingIds);

public interface ILegacyMappedBatchReader
{
    Task ReadMappedBatchesAsync(
        string connectionString,
        string libraryRoot,
        Func<IReadOnlyList<LegacyLocalLinkRow>, LegacyMappedReadResult, Task> onBatch,
        int batchSize = 128,
        CancellationToken cancellationToken = default);
}

public sealed class LegacyNzbDavReader : ILegacyMappedBatchReader
{
    public const string ConnectionEnvironmentVariable = "NZBDAV_MIGRATION_LEGACY_DB";

    public const string ItemQuery = """
        WITH RECURSIVE ancestry AS (
            SELECT d."Id" AS leaf, d."Id", d."ParentId", d."Path",
                   ARRAY[d."Id"] AS visited, false AS cycle, 0 AS depth
            FROM "DavItems" d WHERE d."Id" = ANY (@ids)
            UNION ALL
            SELECT a.leaf, p."Id", p."ParentId", p."Path",
                   a.visited || p."Id", p."Id" = ANY(a.visited), a.depth + 1
            FROM ancestry a JOIN "DavItems" p ON p."Id" = a."ParentId"
            WHERE NOT a.cycle AND a.depth < 128
        ), state AS (
            SELECT a.leaf, bool_or(a.cycle OR (a.depth = 128 AND a."ParentId" IS NOT NULL)
                OR (a."ParentId" IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM "DavItems" p WHERE p."Id" = a."ParentId"))) AS invalid
            FROM ancestry a GROUP BY a.leaf
        ), owners AS (
            SELECT DISTINCT a.leaf, h."Id", a."Path" AS root_path
            FROM ancestry a JOIN "HistoryItems" h ON h."DownloadDirId" = a."Id"
        ), ownership AS (
            SELECT leaf, count(*) AS owner_count, (array_agg("Id"))[1] AS owner_id,
                   min(root_path) AS root_path
            FROM owners GROUP BY leaf
        )
        SELECT d."Id", d."Path", d."FileSize", d."Type", h."Id",
               h."FileName", h."JobName",
               h."Category", h."DownloadStatus", nf."SegmentIds",
               rf."RarParts", mf."Metadata", h."NzbContents", o.root_path,
               CASE WHEN o.owner_count > 1 THEN 'ambiguous-history'
                    WHEN h."Id" IS NULL THEN 'missing-history' END,
               CASE WHEN s.invalid THEN 'invalid-ancestry'
                    WHEN d."IsCorrupted" OR d."RepairStatus" <> 0 OR d."ZeroPadCorruptSegments"
                      OR lower(trim(d."HealthCheckQueueReason")) = 'source-validation'
                      OR EXISTS (SELECT 1 FROM "SourceValidationBlocks" b
                          WHERE b."DavItemId" = d."Id" AND b."Status" IN (1,2,3,5))
                    THEN 'legacy-health-excluded' END
        FROM "DavItems" AS d
        JOIN state s ON s.leaf = d."Id"
        LEFT JOIN ownership o ON o.leaf = d."Id"
        LEFT JOIN "HistoryItems" AS h ON h."Id" = o.owner_id AND o.owner_count = 1 AND NOT s.invalid
        LEFT JOIN "DavNzbFiles" AS nf ON nf."Id" = d."Id"
        LEFT JOIN "DavRarFiles" AS rf ON rf."Id" = d."Id"
        LEFT JOIN "DavMultipartFiles" AS mf ON mf."Id" = d."Id"
        WHERE d."Id" = ANY (@ids)
        ORDER BY d."Id"
        """;

    public async Task<LegacyReadResult> ReadAsync(
        IEnumerable<Guid> requestedIds,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");
        return await ReadAsync(connectionString, requestedIds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LegacyReadResult> ReadAsync(
        string connectionString,
        IEnumerable<Guid> requestedIds,
        CancellationToken cancellationToken = default)
    {
        var ids = requestedIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new LegacyReadResult([], []);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        var rows = await ReadItemsAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AccountForRequestedIds(ids, rows);
    }

    public async Task<LegacyMappedReadResult> ReadMappedAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");
        return await ReadMappedAsync(connectionString, libraryRoot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LegacyMappedReadResult> ReadMappedAsync(
        string connectionString,
        string libraryRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        const string mappingQuery = """
            SELECT "LinkPath", "DavItemId", "IsBroken"
            FROM "LocalLinks"
            WHERE left("LinkPath", length(@prefix)) = @prefix
            ORDER BY "LinkPath", "DavItemId"
            """;
        var links = new List<LegacyLocalLinkRow>();
        await using (var command = new NpgsqlCommand(mappingQuery, connection, transaction))
        {
            command.Parameters.AddWithValue("prefix", root + Path.DirectorySeparatorChar);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                links.Add(new LegacyLocalLinkRow(reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2)));
        }
        var ids = links.Select(link => link.DavItemId).Distinct().ToArray();
        var rows = await ReadItemsAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var accounted = AccountForRequestedIds(ids, rows);
        return new LegacyMappedReadResult(links, accounted.Items, accounted.MissingIds);
    }

    /// <summary>
    /// Reads one repeatable-read, SELECT-only mapping snapshot without retaining the
    /// article metadata for every mapped file at once. The callback must finish
    /// before the next batch is read; an exception aborts the source transaction.
    /// </summary>
    public async Task ReadMappedBatchesAsync(
        string connectionString,
        string libraryRoot,
        Func<IReadOnlyList<LegacyLocalLinkRow>, LegacyMappedReadResult, Task> onBatch,
        int batchSize = 128,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBatch);
        if (batchSize is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        var root = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        const string mappingQuery = """
            SELECT "LinkPath", "DavItemId", "IsBroken"
            FROM "LocalLinks"
            WHERE left("LinkPath", length(@prefix)) = @prefix
            ORDER BY "LinkPath", "DavItemId"
            """;
        var links = new List<LegacyLocalLinkRow>();
        await using (var command = new NpgsqlCommand(mappingQuery, connection, transaction))
        {
            command.Parameters.AddWithValue("prefix", root + Path.DirectorySeparatorChar);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                links.Add(new LegacyLocalLinkRow(reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2)));
        }
        links.Sort((left, right) => string.CompareOrdinal(left.LinkPath, right.LinkPath));

        foreach (var batch in links.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = batch.Select(link => link.DavItemId).Distinct().ToArray();
            var rows = await ReadItemsAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
            var accounted = AccountForRequestedIds(ids, rows);
            await onBatch(links, new LegacyMappedReadResult(batch, accounted.Items, accounted.MissingIds))
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssertMappedLinkAsync(
        string linkPath,
        Guid expectedDavItemId,
        CancellationToken cancellationToken = default)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{ConnectionEnvironmentVariable} is required.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginReadOnlyTransactionAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT \"DavItemId\", \"IsBroken\" FROM \"LocalLinks\" WHERE \"LinkPath\" = @path",
            connection, transaction);
        command.Parameters.AddWithValue("path", linkPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.GetGuid(0) != expectedDavItemId || reader.GetBoolean(1)
            || await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Planned source link is no longer a unique, unbroken LocalLinks mapping.");
    }

    private static async Task<NpgsqlTransaction> BeginReadOnlyTransactionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken)
                .ConfigureAwait(false);
            await RequireReadOnlyTransactionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RequireSelectOnlyLoginAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<List<LegacyDavItemRow>> ReadItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid[] ids,
        CancellationToken cancellationToken)
    {
        var rows = new List<LegacyDavItemRow>();
        foreach (var batch in ids.Chunk(256))
        {
            await using var command = new NpgsqlCommand(ItemQuery, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = batch,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var fileSizeNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
                var historyIdNull = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);
                var historyStatusNull = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false);
                string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
                rows.Add(new LegacyDavItemRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    fileSizeNull ? null : reader.GetInt64(2),
                    reader.GetInt32(3),
                    historyIdNull ? null : reader.GetGuid(4),
                    historyIdNull ? null : reader.GetGuid(4),
                    Text(5), Text(6), Text(7), historyStatusNull ? null : reader.GetInt32(8),
                    Text(9), Text(10), Text(11), Text(12), Text(13),
                    Text(15), Text(14), Text(15)));
            }
        }

        return rows;
    }

    public static LegacyReadResult AccountForRequestedIds(
        IEnumerable<Guid> requestedIds,
        IEnumerable<LegacyDavItemRow> rows)
    {
        var materialized = rows.ToArray();
        var found = materialized.Select(row => row.Id).ToHashSet();
        var missing = requestedIds.Distinct().Where(id => !found.Contains(id)).Order().ToArray();
        return new LegacyReadResult(materialized, missing);
    }

    private static async Task RequireReadOnlyTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SHOW transaction_read_only", connection, transaction);
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The legacy database transaction is not read-only.");
    }

    private static async Task RequireSelectOnlyLoginAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.rolsuper OR EXISTS (
                SELECT 1
                FROM information_schema.tables AS t
                WHERE t.table_schema = ANY (current_schemas(false))
                  AND has_table_privilege(
                      current_user,
                      format('%I.%I', t.table_schema, t.table_name),
                      'INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER'))
            FROM pg_roles AS r
            WHERE r.rolname = current_user
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            throw new InvalidOperationException("The legacy database login is write-capable; use a SELECT-only login.");
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
