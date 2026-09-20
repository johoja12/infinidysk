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
    Guid? FileBlobId,
    Guid? NzbBlobId,
    string? HistoryFileName = null,
    string? HistoryJobName = null,
    string? HistoryCategory = null,
    int? HistoryDownloadStatus = null,
    string? NzbSegmentsJson = null,
    string? RarPartsJson = null,
    string? MultipartMetadataJson = null);

public sealed record LegacyReadResult(
    IReadOnlyList<LegacyDavItemRow> Items,
    IReadOnlyList<Guid> MissingIds);

public sealed class LegacyNzbDavReader
{
    public const string ConnectionEnvironmentVariable = "NZBDAV_MIGRATION_LEGACY_DB";

    public const string ItemQuery = """
        SELECT d."Id", d."Path", d."FileSize", d."Type", d."HistoryItemId",
               d."FileBlobId", d."NzbBlobId", h."FileName", h."JobName",
               h."Category", h."DownloadStatus", nf."SegmentIds",
               rf."RarParts", mf."Metadata"
        FROM "DavItems" AS d
        LEFT JOIN "HistoryItems" AS h ON h."Id" = d."HistoryItemId"
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
        await using var transaction = await connection
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "SET TRANSACTION READ ONLY", cancellationToken)
            .ConfigureAwait(false);
        await RequireReadOnlyTransactionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await RequireSelectOnlyLoginAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        var rows = new List<LegacyDavItemRow>();
        await using (var command = new NpgsqlCommand(ItemQuery, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = ids,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var fileSizeNull = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false);
                var historyIdNull = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false);
                var fileBlobIdNull = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false);
                var nzbBlobIdNull = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false);
                var historyFileNull = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false);
                var historyJobNull = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false);
                var historyCategoryNull = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false);
                var historyStatusNull = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false);
                var nzbSegmentsNull = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false);
                var rarPartsNull = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false);
                var multipartMetadataNull = await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false);
                rows.Add(new LegacyDavItemRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    fileSizeNull ? null : reader.GetInt64(2),
                    reader.GetInt32(3),
                    historyIdNull ? null : reader.GetGuid(4),
                    fileBlobIdNull ? null : reader.GetGuid(5),
                    nzbBlobIdNull ? null : reader.GetGuid(6),
                    historyFileNull ? null : reader.GetString(7),
                    historyJobNull ? null : reader.GetString(8),
                    historyCategoryNull ? null : reader.GetString(9),
                    historyStatusNull ? null : reader.GetInt32(10),
                    nzbSegmentsNull ? null : reader.GetString(11),
                    rarPartsNull ? null : reader.GetString(12),
                    multipartMetadataNull ? null : reader.GetString(13)));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return AccountForRequestedIds(ids, rows);
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
