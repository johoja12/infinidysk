using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Provenance;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class NzbDavLedgerEvidenceTests
{
    [Fact]
    public async Task RecordCompletedAsync_PreservesPackageDigestWhenWritingCorrelation()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using var ledger = harness.Mig();
        await using var dav = harness.Dav();
        var digest = new string('a', 64);
        var storeRef = "nzbdav:release";
        var nzoId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var session = await UsenetMigrationStore.GetOrCreateSessionAsync(ledger);
        session.SourceType = MigrationSourceTypes.NzbDav;
        ledger.Releases.Add(new MigrationRelease
        {
            StoreRef = storeRef,
            StoreBasename = "release",
            SubmitFileName = "release.nzb",
            QueueFileName = "release.nzb",
            JobName = "release",
            ScannedAt = DateTime.UtcNow,
        });
        var source = new MigrationReleaseFile
        {
            StoreRef = storeRef,
            MetaPath = "payloads/release.nzb",
            VirtualPath = "/content/tv/release/video.mkv",
            FileName = "video.mkv",
            NormalisedName = "video.mkv",
            FileSize = 42,
            SourceFileId = Guid.NewGuid().ToString(),
            Flags = JsonSerializer.Serialize(new { packageSha256 = digest, payloadLength = 123 }),
        };
        ledger.ReleaseFiles.Add(source);
        await ledger.SaveChangesAsync();
        var provider = new StubCorrelationProvider(targetId, nzoId);
        var service = new MigrationProvenanceService(new MigrationCorrelationDispatcher([provider]));
        var submission = new MigrationSubmission { StoreRef = storeRef, NzoId = nzoId.ToString() };
        var history = new HistoryItem
        {
            Id = nzoId,
            CreatedAt = DateTime.UtcNow,
            FileName = "release.nzb",
            JobName = "release",
            Category = "tv",
            DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
        };

        Assert.Equal(1, await service.RecordCompletedAsync(ledger, dav, submission, nzoId, history));
        await ledger.SaveChangesAsync();
        await ledger.Entry(source).ReloadAsync();

        using var document = JsonDocument.Parse(source.Flags!);
        Assert.Equal(digest, document.RootElement.GetProperty("packageSha256").GetString());
        Assert.Equal(123, document.RootElement.GetProperty("payloadLength").GetInt32());
        Assert.Equal("article-identity",
            document.RootElement.GetProperty("correlation").GetProperty("match").GetString());
    }

    [Fact]
    public void WithCorrelation_PreservesVerifiedPackageEvidence()
    {
        var digest = new string('a', 64);
        var existing = JsonSerializer.Serialize(new
        {
            packageSha256 = digest,
            payloadLength = 42,
        });

        var merged = NzbDavLedgerEvidence.WithCorrelation(
            existing,
            """{"match":"article-identity","candidates":[]}""");

        using var document = JsonDocument.Parse(merged);
        Assert.Equal(digest, document.RootElement.GetProperty("packageSha256").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("payloadLength").GetInt32());
        Assert.Equal(
            "article-identity",
            document.RootElement.GetProperty("correlation").GetProperty("match").GetString());
    }

    [Theory]
    [InlineData("not-json", "{}")]
    [InlineData("[]", "{}")]
    [InlineData("{}", "{}")]
    [InlineData("{\"packageSha256\":\"short\"}", "{}")]
    [InlineData("{\"packageSha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", "{}")]
    [InlineData("{\"packageSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "[]")]
    public void WithCorrelation_RejectsUnverifiableEvidence(string existing, string correlation)
    {
        Assert.Throws<InvalidDataException>(() =>
            NzbDavLedgerEvidence.WithCorrelation(existing, correlation));
    }

    private sealed class StubCorrelationProvider(Guid targetId, Guid nzoId) : IMigrationCorrelationProvider
    {
        public string SourceType => MigrationSourceTypes.NzbDav;

        public Task<IReadOnlyList<MigrationCorrelationResult>> CorrelateAsync(
            IReadOnlyList<MigrationReleaseFile> sourceFiles,
            IReadOnlyList<DavItem> importedLeaves,
            DavDatabaseContext davContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MigrationCorrelationResult>>(
                sourceFiles.Select(source => new MigrationCorrelationResult(
                    source.Id,
                    "exact",
                    targetId,
                    nzoId,
                    "article-identity",
                    """{"match":"article-identity","candidates":[]}""")).ToArray());
    }
}
