using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Runner;

namespace NzbWebDAV.Tests.UsenetMigration;

public sealed class MigrationScanDispatcherTests
{
    [Fact]
    public void Resolve_SelectsScannerByExactSourceType()
    {
        var altmount = new FakeScanner(MigrationSourceTypes.Altmount);
        var nzbdav = new FakeScanner(MigrationSourceTypes.NzbDav);
        var dispatcher = new MigrationScanDispatcher([altmount, nzbdav]);

        Assert.Same(altmount, dispatcher.Resolve(MigrationSourceTypes.Altmount));
        Assert.Same(nzbdav, dispatcher.Resolve(MigrationSourceTypes.NzbDav));
    }

    [Fact]
    public async Task UnknownSource_FailsBeforeExistingScanArtifactsAreCleared()
    {
        await using var harness = await MigrationTestHarness.CreateAsync();
        await using (var db = harness.Mig())
        {
            db.Releases.Add(new MigrationRelease
            {
                StoreRef = "existing",
                StoreBasename = "existing",
                SubmitFileName = "existing.nzb",
                QueueFileName = "existing.nzb",
                JobName = "existing",
                Verdict = "green",
                VerdictReasons = "[]",
                ScannedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var dispatcher = new MigrationScanDispatcher([new FakeScanner(MigrationSourceTypes.Altmount)]);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Resolve("unknown"));

        await using var verify = harness.Mig();
        Assert.Equal("existing", (await verify.Releases.SingleAsync()).StoreRef);
    }

    private sealed class FakeScanner(string sourceType) : IUsenetMigrationScanRunner
    {
        public string SourceType { get; } = sourceType;
        public Task<ScanSummary?> ScanAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScanSummary?>(new ScanSummary());
    }
}
