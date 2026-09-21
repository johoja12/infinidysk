using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavMigrationSchemaTests
{
    private const string InitialMigration = "20260730182743_InitializeUsenetMigrationDatabase";

    [Fact]
    public async Task FreshSchema_PersistsNzbDavSessionIdentityAndCanaryLinks()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using var db = harness.Mig();
        var now = DateTime.UtcNow;
        db.SessionState.Add(new MigrationSessionState
        {
            SourceType = MigrationSourceTypes.NzbDav,
            SourcePackageRoot = "/config/import/package",
            CanaryLibraryRoot = "/mnt/plex2",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.MigrationRuns.Add(new MigrationRun
        {
            Id = 7,
            SourceType = MigrationSourceTypes.NzbDav,
            Status = "completed",
            StartedAt = now,
            CompletedAt = now,
        });
        db.CanaryLinks.Add(new MigrationCanaryLink
        {
            RunId = 7,
            LibraryRelativePath = "TV/Show/episode.mkv",
            OriginalLegacyTarget = "/mnt/nzbdav/.ids/old",
            NewRelativeTarget = ".ids/a/b/c/d/e/new",
            CorrelationStatus = "exact",
            CorrelationEvidence = "{}",
            SourcePackageDigest = new string('a', 64),
            ExpectedFileSize = 123,
            ApplyStatus = "planned",
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var session = await db.SessionState.SingleAsync();
        Assert.Equal(MigrationSourceTypes.NzbDav, session.SourceType);
        Assert.Equal("/config/import/package", session.SourcePackageRoot);
        Assert.Equal("/mnt/plex2", session.CanaryLibraryRoot);
        var link = await db.CanaryLinks.SingleAsync();
        Assert.Equal("exact", link.CorrelationStatus);
        Assert.Equal(123, link.ExpectedFileSize);
    }

    [Fact]
    public async Task Upgrade_IsAdditiveAndPreservesExistingAltMountRows()
    {
        var path = Path.Join(Path.GetTempPath(), $"nzbdav-schema-{Guid.NewGuid():N}.db");
        var options = MigrationTestHarness.CreateMigrationOptions(path);
        try
        {
            await using (var oldDb = new UsenetMigrationDbContext(options))
            {
                var migrator = oldDb.Database.GetService<IMigrator>();
                await migrator.MigrateAsync(InitialMigration);
                await oldDb.Database.ExecuteSqlRawAsync("""
                    INSERT INTO SessionState (Id, Status, MaxQueueDepth, SubmitWorkers, CreatedAt, UpdatedAt)
                    VALUES (1, 'idle', 20, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    INSERT INTO MigrationPreferences (Id, MaxQueueDepth, SubmitWorkers, UpdatedAt)
                    VALUES (1, 20, 1, CURRENT_TIMESTAMP);
                    INSERT INTO MigrationRuns (Id, SourceType, Status, StartedAt)
                    VALUES (1, 'altmount', 'completed', CURRENT_TIMESTAMP);
                    INSERT INTO MigratedReleases
                        (Id, SourceType, SourceReleaseId, FirstRunId, LastRunId, ExpectedFileCount,
                         MappedFileCount, MigratedAt, LastVerifiedAt)
                    VALUES (1, 'altmount', 'legacy-release', 1, 1, 1, 1, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                    INSERT INTO MigratedFiles
                        (Id, MigratedReleaseId, VirtualPath, NormalisedRelativePath, NormalisedName,
                         FileSize, DavItemId, MatchMethod, LastVerifiedAt)
                    VALUES (1, 1, '/legacy/a.mkv', 'a.mkv', 'a.mkv', 123,
                            '11111111-1111-1111-1111-111111111111', 'exact', CURRENT_TIMESTAMP);
                    """);
            }

            await using (var upgraded = new UsenetMigrationDbContext(options))
            {
                await upgraded.Database.MigrateAsync();
                var session = await upgraded.SessionState.SingleAsync();
                var preferences = await upgraded.Preferences.SingleAsync();
                var migrated = await upgraded.MigratedFiles.SingleAsync();

                Assert.Equal(MigrationSourceTypes.Altmount, session.SourceType);
                Assert.Equal(MigrationSourceTypes.Altmount, preferences.SourceType);
                Assert.Equal("/legacy/a.mkv", migrated.VirtualPath);
                Assert.Null(migrated.SourceFileId);
                Assert.Null(migrated.ArticleIdentityKind);
                Assert.Null(migrated.ArticleIdentityDigest);
                Assert.Empty(upgraded.CanaryLinks);
                Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
            }

            await using var scriptDb = new UsenetMigrationDbContext(options);
            var sql = scriptDb.Database.GetService<IMigrator>().GenerateScript(
                InitialMigration, null, MigrationsSqlGenerationOptions.Default);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task FreshSchema_PersistsOrderedNzbDavMasterBatchesWithUniqueProvenance()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using var db = harness.Mig();
        var now = DateTime.UtcNow;
        var master = new MigrationNzbDavMaster
        {
            ManifestDigest = new string('a', 64),
            SourceLinkCount = 100,
            RecoverableCount = 95,
            Status = "planned",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.NzbDavMasters.Add(master);
        await db.SaveChangesAsync();
        db.NzbDavBatches.Add(new MigrationNzbDavBatch
        {
            MasterId = master.Id,
            BatchIndex = 0,
            PackageDigest = new string('b', 64),
            SelectionCount = 95,
            Status = "pending",
            PlanDigest = new string('c', 64),
            AppliedCount = 0,
            ValidatedCount = 0,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var stored = await db.NzbDavMasters.SingleAsync();
        var batch = await db.NzbDavBatches.SingleAsync();
        Assert.Equal(95, stored.RecoverableCount);
        Assert.Equal(stored.Id, batch.MasterId);
        Assert.Equal(95, batch.SelectionCount);
        Assert.Equal(new string('c', 64), batch.PlanDigest);

        var batchEntity = db.Model.FindEntityType(typeof(MigrationNzbDavBatch))!;
        Assert.Contains(batchEntity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(MigrationNzbDavBatch.MasterId), nameof(MigrationNzbDavBatch.BatchIndex)]));
        Assert.Contains(batchEntity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(MigrationNzbDavBatch.PackageDigest)]));
    }
}
