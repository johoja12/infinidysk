using NzbDavMigration.Legacy;
using Npgsql;
using NzbWebDAV.Database;
using NzbDavMigration.Export;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.UsenetMigration.NzbDav;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class LegacyNzbDavReaderTests
{
    [Fact]
    public void Query_UsesObservedTypeColumnAndDoesNotAssumeSubType()
    {
        Assert.Contains("d.\"Type\"", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("SubType", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
        Assert.Contains("ANY", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequestedRows_AreReportedExplicitly()
    {
        var found = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var missing = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rows = new[] { new LegacyDavItemRow(found, "/content/a.mkv", 123, 2, null, null, null) };

        var result = LegacyNzbDavReader.AccountForRequestedIds([found, missing], rows);

        Assert.Single(result.Items);
        Assert.Equal(missing, Assert.Single(result.MissingIds));
    }

    [Fact]
    public async Task IdentityExtractor_UsesOrderedArticlesAndExcludesMissingMetadata()
    {
        var fixture = Path.Join(AppContext.BaseDirectory, "Fixtures", "UsenetMigration", "direct-sample.nzb");
        await using var stream = File.OpenRead(fixture);
        var document = await NzbDocument.LoadAsync(stream);
        var blobId = Guid.NewGuid();
        var ready = new LegacyDavItemRow(Guid.NewGuid(), "/content/Canary.Direct.mkv", 220, 3,
            null, null, blobId, NzbSegmentsJson: "[\"direct-1@test\",\"direct-2@test\"]");

        var extracted = new LegacyIdentityExtractor().Extract(ready, document, blobId.ToString());
        var excluded = new LegacyIdentityExtractor().Extract(
            ready with { Id = Guid.NewGuid(), NzbSegmentsJson = null }, document, blobId.ToString());

        Assert.Equal("ready", extracted.ExtractionStatus);
        Assert.Equal(NzbDavArticleIdentity.DirectKind, extracted.IdentityKind);
        Assert.Equal(NzbDavArticleIdentity.ComputeDirect([
            new NzbDavArticleSegment(1, 100, "direct-1@test"),
            new NzbDavArticleSegment(2, 120, "direct-2@test"),
        ]), extracted.IdentityDigest);
        Assert.Equal("excluded", excluded.ExtractionStatus);
        Assert.Contains("identity-unavailable", excluded.ExclusionReason, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ReadAsync_UsesSelectOnlyLegacyTypeSchemaAndPreservesHistoryPresence()
    {
        Skip.IfNot(DatabaseProviderConfig.IsPostgres,
            "Legacy reader integration requires DATABASE_PROVIDER=postgres.");
        var schema = $"legacy_export_{Guid.NewGuid():N}";
        var role = $"legacy_reader_{Guid.NewGuid():N}";
        var password = Guid.NewGuid().ToString("N");
        var withHistory = Guid.NewGuid();
        var withoutHistory = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var blobId = Guid.NewGuid();
        var roleCreated = false;

        await using var admin = new NpgsqlConnection(DatabaseProviderConfig.PostgresConnectionString);
        await admin.OpenAsync();
        try
        {
            await ExecuteAsync(admin, $"CREATE SCHEMA \"{schema}\"");
            await ExecuteAsync(admin, $"""
                CREATE TABLE "{schema}"."HistoryItems" (
                    "Id" uuid PRIMARY KEY, "FileName" text NOT NULL, "JobName" text NOT NULL,
                    "Category" text NOT NULL, "DownloadStatus" integer NOT NULL);
                CREATE TABLE "{schema}"."DavItems" (
                    "Id" uuid PRIMARY KEY, "Path" text NOT NULL, "FileSize" bigint,
                    "Type" integer NOT NULL, "HistoryItemId" uuid, "FileBlobId" uuid, "NzbBlobId" uuid);
                CREATE TABLE "{schema}"."DavNzbFiles" ("Id" uuid PRIMARY KEY, "SegmentIds" text);
                CREATE TABLE "{schema}"."DavRarFiles" ("Id" uuid PRIMARY KEY, "RarParts" text);
                CREATE TABLE "{schema}"."DavMultipartFiles" ("Id" uuid PRIMARY KEY, "Metadata" text);
                INSERT INTO "{schema}"."HistoryItems" VALUES ('{historyId}', 'a.nzb', 'a', 'tv', 1);
                INSERT INTO "{schema}"."DavItems" VALUES
                    ('{withHistory}', '/content/a.mkv', 123, 3, '{historyId}', NULL, '{blobId}'),
                    ('{withoutHistory}', '/content/b.mkv', 456, 3, NULL, NULL, '{blobId}');
                INSERT INTO "{schema}"."DavNzbFiles" VALUES
                    ('{withHistory}', '[\"one@example\"]'), ('{withoutHistory}', '[\"two@example\"]');
                """);
            await ExecuteAsync(admin, $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}'");
            roleCreated = true;
            await ExecuteAsync(admin, $"""
                GRANT CONNECT ON DATABASE "{admin.Database}" TO "{role}";
                GRANT USAGE ON SCHEMA "{schema}" TO "{role}";
                GRANT SELECT ON ALL TABLES IN SCHEMA "{schema}" TO "{role}";
                """);
            var builder = new NpgsqlConnectionStringBuilder(DatabaseProviderConfig.PostgresConnectionString)
            {
                Username = role,
                Password = password,
                SearchPath = schema,
                Pooling = false,
            };

            var result = await new LegacyNzbDavReader().ReadAsync(
                builder.ConnectionString, [withHistory, withoutHistory, missing]);

            Assert.Equal(2, result.Items.Count);
            Assert.NotNull(result.Items.Single(item => item.Id == withHistory).HistoryItemId);
            Assert.Null(result.Items.Single(item => item.Id == withoutHistory).HistoryItemId);
            Assert.Equal(missing, Assert.Single(result.MissingIds));
        }
        finally
        {
            await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            if (roleCreated)
            {
                await ExecuteAsync(admin, $"DROP OWNED BY \"{role}\"");
                await ExecuteAsync(admin, $"DROP ROLE IF EXISTS \"{role}\"");
            }
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
