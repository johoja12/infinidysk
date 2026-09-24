using NzbDavMigration.Legacy;
using Npgsql;
using NzbWebDAV.Database;
using NzbDavMigration.Export;
using NzbDavMigration.Inventory;
using NzbDavMigration.Catalogue;
using NzbDavMigration.Recovery;
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
        Assert.DoesNotContain("d.\"HistoryItemId\"", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FileBlobId\"", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("AND h.\"Id\" IS NOT NULL", LegacyNzbDavReader.ItemQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequestedRows_AreReportedExplicitly()
    {
        var found = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var missing = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var rows = new[] { new LegacyDavItemRow(found, "/content/a.mkv", 123, 2, null, null) };

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
            null, blobId, NzbSegmentsJson: "[\"direct-1@test\",\"direct-2@test\"]");

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
        var releaseId = Guid.NewGuid();
        var nestedId = Guid.NewGuid();
        var roleCreated = false;

        await using var admin = new NpgsqlConnection(DatabaseProviderConfig.PostgresConnectionString);
        await admin.OpenAsync();
        try
        {
            await ExecuteAsync(admin, $"CREATE SCHEMA \"{schema}\"");
            await ExecuteAsync(admin, $"""
                CREATE TABLE "{schema}"."HistoryItems" (
                    "Id" uuid PRIMARY KEY, "FileName" text NOT NULL, "JobName" text NOT NULL,
                    "Category" text NOT NULL, "DownloadStatus" integer NOT NULL,
                    "DownloadDirId" uuid, "NzbContents" text);
                CREATE TABLE "{schema}"."DavItems" (
                    "Id" uuid PRIMARY KEY, "Path" text NOT NULL, "FileSize" bigint,
                    "Type" integer NOT NULL, "ParentId" uuid,
                    "IsCorrupted" boolean NOT NULL DEFAULT false, "RepairStatus" integer NOT NULL DEFAULT 0,
                    "ZeroPadCorruptSegments" boolean NOT NULL DEFAULT false, "HealthCheckQueueReason" text);
                CREATE TABLE "{schema}"."DavNzbFiles" ("Id" uuid PRIMARY KEY, "SegmentIds" text);
                CREATE TABLE "{schema}"."DavRarFiles" ("Id" uuid PRIMARY KEY, "RarParts" text);
                CREATE TABLE "{schema}"."DavMultipartFiles" ("Id" uuid PRIMARY KEY, "Metadata" text);
                CREATE TABLE "{schema}"."LocalLinks" (
                    "LinkPath" text PRIMARY KEY, "DavItemId" uuid NOT NULL, "IsBroken" boolean NOT NULL);
                CREATE TABLE "{schema}"."SourceValidationBlocks" ("DavItemId" uuid, "Status" integer);
                INSERT INTO "{schema}"."HistoryItems" VALUES ('{historyId}', 'a.nzb', 'wrong-job-name', 'tv', 1, '{releaseId}', '<nzb />');
                INSERT INTO "{schema}"."DavItems" ("Id", "Path", "FileSize", "Type", "ParentId") VALUES
                    ('{releaseId}', '/content/release', NULL, 1, NULL),
                    ('{nestedId}', '/content/release/nested', NULL, 1, '{releaseId}'),
                    ('{withHistory}', '/content/release/nested/a.mkv', 123, 3, '{nestedId}'),
                    ('{withoutHistory}', '/content/b.mkv', 456, 3, NULL);
                INSERT INTO "{schema}"."DavNzbFiles" VALUES
                    ('{withHistory}', '["one@example"]'), ('{withoutHistory}', '["two@example"]');
                INSERT INTO "{schema}"."LocalLinks" VALUES
                    ('/mnt/plex/a.mkv', '{withHistory}', false),
                    ('/mnt/plex/b.mkv', '{withoutHistory}', true),
                    ('/mnt/special/c.mkv', '{withHistory}', false);
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
            var mapped = await new LegacyNzbDavReader().ReadMappedAsync(
                builder.ConnectionString, "/mnt/plex");
            var batches = new List<LegacyMappedReadResult>();
            await new LegacyNzbDavReader().ReadMappedBatchesAsync(
                builder.ConnectionString, "/mnt/plex", (allMappings, batch) =>
                {
                    Assert.Equal(2, allMappings.Count);
                    batches.Add(batch);
                    return Task.CompletedTask;
                }, batchSize: 1);
            Assert.Equal(2, batches.Count);
            Assert.All(batches, batch => Assert.Single(batch.Links));
            Assert.Equal(2, batches.Sum(batch => batch.Items.Count));
            Assert.Equal(2, mapped.Links.Count);
            Assert.Equal(2, mapped.Items.Count);
            Assert.True(mapped.Links.Single(link => link.DavItemId == withoutHistory).IsBroken);
            Assert.All(mapped.Links, link => Assert.StartsWith("/mnt/plex/", link.LinkPath, StringComparison.Ordinal));

            Assert.Equal(2, result.Items.Count);
            Assert.NotNull(result.Items.Single(item => item.Id == withHistory).HistoryItemId);
            Assert.Null(result.Items.Single(item => item.Id == withoutHistory).HistoryItemId);
            Assert.Equal(missing, Assert.Single(result.MissingIds));
            Assert.Equal(historyId, result.Items.Single(item => item.Id == withHistory).NzbBlobId);
            Assert.Equal("/content/release", result.Items.Single(item => item.Id == withHistory).ReleaseRootPath);
            Assert.Equal("<nzb />", result.Items.Single(item => item.Id == withHistory).NzbContents);
            var orphan = result.Items.Single(item => item.Id == withoutHistory);
            Assert.Null(orphan.HistoryItemId);
            Assert.Equal("[\"two@example\"]", orphan.NzbSegmentsJson);
            Assert.Equal("missing-history", orphan.HistoryExclusion);
            Assert.Null(orphan.SafetyExclusion);
            Assert.Null(orphan.ResolutionExclusion);

            var shardFixture = Path.Join(Path.GetTempPath(), $"mapped-shards-{Guid.NewGuid():N}");
            try
            {
                var sourceRoot = Path.Join(shardFixture, "library");
                var idsRoot = Path.Join(shardFixture, ".ids");
                var outputRoot = Path.Join(shardFixture, "inventory");
                Directory.CreateDirectory(sourceRoot);
                Directory.CreateDirectory(idsRoot);
                Directory.CreateDirectory(Path.Join(shardFixture, "blobs"));
                File.CreateSymbolicLink(Path.Join(sourceRoot, "a.mkv"), Path.Join(idsRoot, withHistory.ToString()));
                File.CreateSymbolicLink(Path.Join(sourceRoot, "b.mkv"), Path.Join(idsRoot, withoutHistory.ToString()));
                await using (var insert = new NpgsqlCommand($"""
                    INSERT INTO "{schema}"."LocalLinks" VALUES
                    (@first, @withHistory, false), (@second, @withoutHistory, false)
                    """, admin))
                {
                    insert.Parameters.AddWithValue("first", Path.Join(sourceRoot, "a.mkv"));
                    insert.Parameters.AddWithValue("withHistory", withHistory);
                    insert.Parameters.AddWithValue("second", Path.Join(sourceRoot, "b.mkv"));
                    insert.Parameters.AddWithValue("withoutHistory", withoutHistory);
                    await insert.ExecuteNonQueryAsync();
                }
                var writer = new ShardedMappedInventoryWriter();
                var manifest = await writer.WriteAsync(sourceRoot, idsRoot,
                    Path.Join(shardFixture, "blobs"), outputRoot, batchSize: 1,
                    connectionString: builder.ConnectionString);
                Assert.Equal(2, manifest.RowCount);
                Assert.Equal(2, manifest.Shards.Count);
                File.Delete(Path.Join(outputRoot, "manifest.json"));
                var resumedInventory = await writer.WriteAsync(sourceRoot, idsRoot,
                    Path.Join(shardFixture, "blobs"), outputRoot, batchSize: 1,
                    connectionString: builder.ConnectionString);
                Assert.Equal(manifest.RowsSha256, resumedInventory.RowsSha256);
                foreach (var shard in manifest.Shards)
                    Assert.Single((await ShardedMappedInventoryWriter.ReadVerifiedShardAsync(
                        outputRoot, manifest, shard)).Rows);

                var allowlist = await ShardedMappedInventoryWriter.ReadAllowlistAsync(outputRoot, manifest);
                Assert.Equal(2, allowlist.Count);
                var cataloguePath = Path.Join(shardFixture, "catalogue.sqlite");
                await using (var store = new OrphanCatalogueStore(
                                 cataloguePath, cataloguePath + ".completion.json"))
                {
                    var inputDigest = new string('f', 64);
                    await store.BeginAsync(inputDigest);
                    foreach (var (id, article, bytes, digest) in new[]
                             {
                                 (withHistory, "one@example", 123L, new string('1', 64)),
                                 (withoutHistory, "two@example", 456L, new string('2', 64)),
                             })
                    {
                        var segments = new[] { new NzbDavArticleSegment(1, bytes, article) };
                        await store.UpsertAsync(new OrphanCatalogueBlob(
                            $"{id}.nzb", 100, 200, digest, "valid", null,
                            NzbDavArticleIdentity.ComputeRelease([segments]),
                            [new OrphanCatalogueArticle(article, 0, 1, bytes)]));
                    }
                    await store.SealAsync(await store.BuildSummaryAsync(inputDigest));
                    var recoveryRoot = Path.Join(shardFixture, "recovery");
                    var recovery = await new ShardedMappedRecovery().WriteAsync(
                        outputRoot, store, recoveryRoot);
                    Assert.Equal(2, recovery.TotalLinks);
                    Assert.Equal(2, recovery.RecoverableLinks);
                    Assert.Equal(1m, recovery.RecoverableFraction);
                    File.Delete(Path.Join(recoveryRoot, "manifest.json"));
                    var resumed = await new ShardedMappedRecovery().WriteAsync(
                        outputRoot, store, recoveryRoot);
                    Assert.Equal(recovery.RecoverableLinks, resumed.RecoverableLinks);
                    var loaded = await new ShardedMappedExporter().LoadRootAsync(outputRoot, recoveryRoot);
                    Assert.Equal(2, loaded.Items.Count);
                }

                await ExecuteAsync(admin, $"""
                    UPDATE "{schema}"."LocalLinks" SET "DavItemId" = '{withoutHistory}'
                    WHERE "LinkPath" = '{Path.Join(sourceRoot, "a.mkv")}'
                    """);
                var changed = await writer.WriteAsync(sourceRoot, idsRoot,
                    Path.Join(shardFixture, "blobs"), Path.Join(shardFixture, "changed"),
                    batchSize: 1, connectionString: builder.ConnectionString);
                Assert.NotEqual(manifest.RowsSha256, changed.RowsSha256);
                await ExecuteAsync(admin, $"""
                    UPDATE "{schema}"."LocalLinks" SET "DavItemId" = '{withHistory}'
                    WHERE "LinkPath" = '{Path.Join(sourceRoot, "a.mkv")}'
                    """);

                var firstPath = Path.Join(outputRoot, manifest.Shards[0].RelativePath);
                await File.AppendAllTextAsync(firstPath, " ");
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    ShardedMappedInventoryWriter.ReadVerifiedShardAsync(
                        outputRoot, manifest, manifest.Shards[0]));
            }
            finally
            {
                if (Directory.Exists(shardFixture)) Directory.Delete(shardFixture, recursive: true);
            }

            foreach (var assignment in new[] { "\"IsCorrupted\" = true", "\"RepairStatus\" = 2",
                         "\"ZeroPadCorruptSegments\" = true", "\"HealthCheckQueueReason\" = ' Source-Validation '" })
            {
                await ExecuteAsync(admin, $"UPDATE \"{schema}\".\"DavItems\" SET {assignment} WHERE \"Id\" = '{withHistory}'");
                var unsafeRows = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]);
                Assert.Equal("legacy-health-excluded", Assert.Single(unsafeRows.Items).ResolutionExclusion);
                await ExecuteAsync(admin, $"UPDATE \"{schema}\".\"DavItems\" SET \"IsCorrupted\" = false, \"RepairStatus\" = 0, \"ZeroPadCorruptSegments\" = false, \"HealthCheckQueueReason\" = NULL");
            }
            foreach (var status in new[] { 1, 2, 3, 5 })
            {
                await ExecuteAsync(admin, $"INSERT INTO \"{schema}\".\"SourceValidationBlocks\" VALUES ('{withHistory}', {status})");
                var blocked = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]);
                Assert.Equal("legacy-health-excluded", Assert.Single(blocked.Items).ResolutionExclusion);
                await ExecuteAsync(admin, $"DELETE FROM \"{schema}\".\"SourceValidationBlocks\"");
            }

            foreach (var parent in new[] { nestedId, Guid.NewGuid() })
            {
                await ExecuteAsync(admin, $"UPDATE \"{schema}\".\"DavItems\" SET \"ParentId\" = '{parent}' WHERE \"Id\" = '{releaseId}'");
                var broken = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]);
                Assert.Equal("invalid-ancestry", Assert.Single(broken.Items).ResolutionExclusion);
            }
            var chain = Enumerable.Range(0, 270).Select(_ => Guid.NewGuid()).ToArray();
            var values = chain.Select((id, index) => $"('{id}', '/chain/{index}', 1, {(index + 1 < chain.Length ? $"'{chain[index + 1]}'::uuid" : "NULL")})");
            await ExecuteAsync(admin, $"INSERT INTO \"{schema}\".\"DavItems\" (\"Id\", \"Path\", \"Type\", \"ParentId\") VALUES {string.Join(',', values)}");
            await ExecuteAsync(admin, $"UPDATE \"{schema}\".\"DavItems\" SET \"ParentId\" = '{chain[0]}' WHERE \"Id\" = '{releaseId}'");
            var bounded = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]);
            Assert.Equal("invalid-ancestry", Assert.Single(bounded.Items).ResolutionExclusion);
            var batched = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, chain.Append(missing));
            Assert.Equal(chain.Length, batched.Items.Count);
            Assert.Equal(missing, Assert.Single(batched.MissingIds));
            await ExecuteAsync(admin, $"UPDATE \"{schema}\".\"DavItems\" SET \"ParentId\" = NULL WHERE \"Id\" = '{releaseId}'");

            await ExecuteAsync(admin, $"INSERT INTO \"{schema}\".\"HistoryItems\" VALUES ('{Guid.NewGuid()}', 'duplicate.nzb', 'duplicate', 'tv', 1, '{releaseId}', NULL)");
            var ambiguous = await new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]);
            Assert.Null(Assert.Single(ambiguous.Items).HistoryItemId);
            Assert.Equal("ambiguous-history", Assert.Single(ambiguous.Items).HistoryExclusion);
            Assert.Null(Assert.Single(ambiguous.Items).ResolutionExclusion);

            await ExecuteAsync(admin, $"GRANT UPDATE ON \"{schema}\".\"DavItems\" TO \"{role}\"");
            var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LegacyNzbDavReader().ReadAsync(builder.ConnectionString, [withHistory]));
            Assert.Contains("SELECT-only", denied.Message, StringComparison.Ordinal);
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
