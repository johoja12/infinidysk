using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDavMigration.Inventory;
using NzbDavMigration.Legacy;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using NzbWebDAV.UsenetMigration.NzbDav;
using NzbWebDAV.UsenetMigration.Source;

namespace NzbDavMigration.Recovery;

internal sealed record FailedImportReport(
    bool Status,
    string PackageDigest,
    int? BatchIndex,
    int FailedCount,
    IReadOnlyList<FailedImportRow> Failures);

internal sealed record ArchivedCorrelation(
    bool Status, string PackageDigest, int SelectedCount, int ExactCount,
    IReadOnlyList<ArchivedCorrelationRow> Rows);

internal sealed record ArchivedCorrelationRow(
    Guid LegacyDavItemId, string LibraryRelativePath, string CorrelationStatus);

internal sealed record ArchivedAcknowledgement(
    bool Status, int BatchIndex, string State, int SelectionCount,
    int AppliedCount, int ValidatedCount);

internal sealed record FailedImportRow(
    string SourceReleaseId,
    Guid LegacyDavItemId,
    string LibraryRelativePath,
    string SubmissionState,
    string? Reason);

internal sealed record FailedImportCleanupEntry(
    Guid DavItemId,
    string SourcePath,
    string Stage);

internal sealed record FailedImportCleanupJournal(
    string PackageDigest,
    List<FailedImportCleanupEntry> Entries);

/// <summary>
/// Runs after a terminal NzbDav migration batch and its failure report have been saved.
/// Each external mutation has a durable journal boundary. An uncertain response stops
/// for review, rather than repeating a deletion or replacement search blindly.
/// </summary>
internal static class FailedImportCleanup
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task RunAsync(
        string reportPath,
        string packagePath,
        string inventoryDirectory,
        string sourceRoot,
        string arrRoot,
        string arrConfigPath,
        string infinidyskUrl,
        string legacyUrl,
        string journalPath,
        bool waitForTerminal,
        string? historicalCorrelationPath = null,
        string? historicalAcknowledgementPath = null,
        bool preflightOnly = false,
        bool skipChangedHistoricalSources = false,
        CancellationToken ct = default)
    {
        var apiKey = Environment.GetEnvironmentVariable("NZBDAV_MIGRATION_LEGACY_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("NZBDAV_MIGRATION_LEGACY_API_KEY is required.");
        var infinidyskApiKey = Environment.GetEnvironmentVariable("INFINIDYSK_MIGRATION_API_KEY");
        if (string.IsNullOrWhiteSpace(infinidyskApiKey))
            throw new InvalidOperationException("INFINIDYSK_MIGRATION_API_KEY is required.");

        var package = await new NzbDavPackageReader().ReadAsync(packagePath, ct).ConfigureAwait(false);
        using var destinationHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        destinationHttp.DefaultRequestHeaders.Add("x-api-key", infinidyskApiKey);
        var destinationUri = new Uri(infinidyskUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var failuresUri = new Uri(destinationUri, "api/migration/nzbdav/import-failures");
        var historical = historicalCorrelationPath is not null
            || historicalAcknowledgementPath is not null;
        if (historical && (historicalCorrelationPath is null
                           || historicalAcknowledgementPath is null || waitForTerminal))
            throw new InvalidDataException("Historical cleanup needs both archived correlation and acknowledgement, without waiting for a current batch.");
        if (skipChangedHistoricalSources && !historical)
            throw new InvalidDataException("Changed-source skipping is only available for archived batches.");
        if (waitForTerminal)
            await WaitForTerminalAndSaveReportAsync(
                destinationHttp, failuresUri, package.PackageDigest, reportPath, ct).ConfigureAwait(false);
        var report = await ReadPrivateJsonAsync<FailedImportReport>(reportPath, ct).ConfigureAwait(false);
        if (!report.Status || report.PackageDigest != package.PackageDigest
            || report.BatchIndex != package.Manifest.BatchIndex
            || report.FailedCount != report.Failures.Count)
            throw new InvalidDataException("Import failure report does not match the verified package.");
        if (report.Failures.Any(row => row.SubmissionState is not ("failed" or "evicted")))
            throw new InvalidDataException("Failure report contains an unsupported submission state.");

        if (historical)
        {
            var correlation = await ReadPrivateJsonAsync<ArchivedCorrelation>(
                historicalCorrelationPath!, ct).ConfigureAwait(false);
            var acknowledgement = await ReadPrivateJsonAsync<ArchivedAcknowledgement>(
                historicalAcknowledgementPath!, ct).ConfigureAwait(false);
            var selectedLinks = package.Manifest.SelectedLinks;
            if (!correlation.Status || correlation.PackageDigest != package.PackageDigest
                || correlation.SelectedCount != selectedLinks.Count
                || correlation.Rows.Count != selectedLinks.Count
                || correlation.Rows.Select(row => row.LegacyDavItemId).Distinct().Count() != selectedLinks.Count
                || !acknowledgement.Status || acknowledgement.BatchIndex != package.Manifest.BatchIndex
                || acknowledgement.State != "acknowledged"
                || acknowledgement.SelectionCount != selectedLinks.Count
                || acknowledgement.AppliedCount != correlation.ExactCount
                || acknowledgement.ValidatedCount != correlation.ExactCount)
                throw new InvalidDataException("Archived batch evidence does not match the verified package.");
            var byId = selectedLinks.ToDictionary(link => link.LegacyDavItemId);
            if (correlation.Rows.Any(row => !byId.TryGetValue(row.LegacyDavItemId, out var link)
                || link.LibraryRelativePath != row.LibraryRelativePath)
                || report.Failures.Any(row => !correlation.Rows.Any(match =>
                    match.LegacyDavItemId == row.LegacyDavItemId
                    && match.CorrelationStatus == "import-failed")))
                throw new InvalidDataException("Archived correlation does not prove the reported import failures.");
        }
        else
        {
            var currentReport = await destinationHttp.GetFromJsonAsync<FailedImportReport>(
                failuresUri, JsonOptions, ct).ConfigureAwait(false);
            if (currentReport is not { Status: true } || currentReport.PackageDigest != report.PackageDigest
                || currentReport.BatchIndex != report.BatchIndex
                || currentReport.FailedCount != report.FailedCount
                || currentReport.Failures.OrderBy(row => row.LegacyDavItemId)
                    .SequenceEqual(report.Failures.OrderBy(row => row.LegacyDavItemId)) == false)
                throw new InvalidDataException("Saved failure report is no longer the current terminal batch report.");
        }

        var expected = package.Manifest.Releases
            .SelectMany(release => release.Leaves.Select(leaf => (release.SourceReleaseId, leaf.LegacyDavItemId)))
            .ToDictionary(item => item.LegacyDavItemId, item => item.SourceReleaseId);
        var selected = package.Manifest.SelectedLinks
            .ToDictionary(link => link.LegacyDavItemId);
        if (report.Failures.Select(row => row.LegacyDavItemId).Distinct().Count() != report.Failures.Count)
            throw new InvalidDataException("Import failure report repeats a legacy item ID.");
        foreach (var row in report.Failures)
        {
            if (!expected.TryGetValue(row.LegacyDavItemId, out var releaseId)
                || releaseId != row.SourceReleaseId
                || !selected.TryGetValue(row.LegacyDavItemId, out var link)
                || link.LibraryRelativePath != row.LibraryRelativePath)
                throw new InvalidDataException("Import failure report contains an item outside the package selection.");
        }

        var inventory = await ShardedMappedInventoryWriter.ReadManifestAsync(inventoryDirectory, ct)
            .ConfigureAwait(false);
        var canonicalSourceRoot = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (inventory.SourceRoot != canonicalSourceRoot)
            throw new InvalidDataException("Mapped inventory and source root disagree.");
        var allowlist = await ShardedMappedInventoryWriter.ReadAllowlistAsync(inventoryDirectory, inventory, ct)
            .ConfigureAwait(false);
        var allowlistByPath = allowlist.ToDictionary(row => row.LinkPath, StringComparer.Ordinal);
        var canonicalArrRoot = Path.GetFullPath(arrRoot).TrimEnd(Path.DirectorySeparatorChar);
        var arrConfig = await ReadPrivateJsonAsync<ArrConfig>(arrConfigPath, ct).ConfigureAwait(false);
        var clients = arrConfig.GetArrClients().ToArray();
        if (clients.Length == 0)
            throw new InvalidDataException("No enabled Radarr or Sonarr instance was supplied.");

        // Eviction does not prove an import failed. Preserve those items for manual
        // reconciliation while cleaning the confirmed failures in the same report.
        var entries = report.Failures.Where(row => row.SubmissionState == "failed")
            .Select(row => new FailedImportCleanupEntry(
            row.LegacyDavItemId,
            Path.Join(canonicalSourceRoot, row.LibraryRelativePath),
            "planned")).ToList();
        FailedImportCleanupJournal journal;
        if (File.Exists(journalPath))
        {
            journal = await ReadPrivateJsonAsync<FailedImportCleanupJournal>(journalPath, ct)
                .ConfigureAwait(false);
            if (journal.PackageDigest != package.PackageDigest
                || journal.Entries.Count != entries.Count
                || journal.Entries.Any(item => !entries.Any(expectedEntry =>
                    item.DavItemId == expectedEntry.DavItemId
                    && item.SourcePath == expectedEntry.SourcePath)))
                throw new InvalidDataException("Cleanup journal belongs to a different failure report.");
        }
        else
        {
            journal = new FailedImportCleanupJournal(package.PackageDigest, entries);
            await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
        }

        var legacyReader = new LegacyNzbDavReader();
        var actions = new List<(FailedImportCleanupEntry Entry, ArrClient Client, ArrMediaFileMatch Match)>();
        foreach (var item in journal.Entries)
        {
            if (item.Stage is "completed" or "skipped_source_changed") continue;
            if (item.Stage is "source_deleting" or "arr_cleanup_started")
                throw new InvalidOperationException(
                    $"Cleanup of {item.DavItemId} has an uncertain external outcome; reconcile it before resuming.");
            if (item.Stage is not ("planned" or "source_deleted"))
                throw new InvalidDataException("Cleanup journal contains an unknown stage.");

            var row = report.Failures.Single(failure => failure.LegacyDavItemId == item.DavItemId);
            var link = selected[item.DavItemId];
            if (!allowlistByPath.TryGetValue(item.SourcePath, out var mapping)
                || mapping.DavItemId != item.DavItemId
                || mapping.IsBroken || mapping.ExclusionReason is not null
                || mapping.OriginalTarget != link.OriginalTarget)
                throw new InvalidDataException("Failed item no longer matches the sealed mapped inventory.");
            if (item.Stage == "planned")
            {
                try
                {
                    await legacyReader.AssertOnlyMappedLinkAsync(item.DavItemId, item.SourcePath, ct)
                        .ConfigureAwait(false);
                    var actualTarget = new FileInfo(item.SourcePath).LinkTarget;
                    if (actualTarget != link.OriginalTarget)
                        throw new InvalidDataException("Failed item source symlink changed since export.");
                }
                catch (InvalidDataException) when (skipChangedHistoricalSources)
                {
                    await SetStageAsync(journalPath, journal, item, "skipped_source_changed", ct)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            var arrPath = Path.Join(canonicalArrRoot, row.LibraryRelativePath);
            var matches = new List<(ArrClient Client, ArrMediaFileMatch Match)>();
            foreach (var client in clients)
            {
                var match = await FindMediaFileWithRetryAsync(client, arrPath, ct)
                    .ConfigureAwait(false);
                if (match is not null) matches.Add((client, match));
            }
            if (matches.Count != 1 || matches[0].Match.MediaIds.Count == 0)
                throw new InvalidDataException(
                    $"Expected one Radarr/Sonarr media file with search targets for {row.LibraryRelativePath}; found {matches.Count}.");
            actions.Add((item, matches[0].Client, matches[0].Match));
        }

        Console.WriteLine($"Cleanup preflight: confirmed={entries.Count}, actionable={actions.Count}, " +
            $"sourceChanged={journal.Entries.Count(entry => entry.Stage == "skipped_source_changed")}");

        if (preflightOnly) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        var baseUri = new Uri(legacyUrl.TrimEnd('/') + "/", UriKind.Absolute);
        foreach (var action in actions)
        {
            var item = action.Entry;
            if (item.Stage == "planned")
            {
                await legacyReader.AssertOnlyMappedLinkAsync(item.DavItemId, item.SourcePath, ct)
                    .ConfigureAwait(false);
                if (new FileInfo(item.SourcePath).LinkTarget
                    != selected[item.DavItemId].OriginalTarget)
                    throw new InvalidDataException("Failed item source symlink changed during cleanup.");
                await SetStageAsync(journalPath, journal, item, "source_deleting", ct).ConfigureAwait(false);
                using var response = await http.PostAsJsonAsync(
                    new Uri(baseUri, "api/stats/delete-files"),
                    new { davItemIds = new[] { item.DavItemId } }, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<LegacyDeleteResult>(JsonOptions, ct)
                    .ConfigureAwait(false);
                if (result is not { Deleted: 1, Failed: 0 })
                    throw new InvalidOperationException("Legacy NzbDav did not confirm deletion of exactly one item.");
                await legacyReader.AssertMappedLinkAbsentAsync(item.DavItemId, item.SourcePath, ct)
                    .ConfigureAwait(false);
                if (new FileInfo(item.SourcePath).LinkTarget is not null
                    || File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
                    throw new InvalidDataException("Legacy NzbDav still has the deleted library link.");
                await SetStageAsync(journalPath, journal, item, "source_deleted", ct).ConfigureAwait(false);
            }

            await SetStageAsync(journalPath, journal, item, "arr_cleanup_started", ct).ConfigureAwait(false);
            var outcome = await action.Client.RemoveMissingPayloadAndSearchAsync(action.Match, ct: ct)
                .ConfigureAwait(false);
            if (outcome != ArrMissingPayloadCleanupOutcome.RemovedSearchRequested)
                throw new InvalidOperationException(
                    $"Radarr/Sonarr cleanup for {item.DavItemId} ended with {outcome}; reconcile before resuming.");
            await SetStageAsync(journalPath, journal, item, "completed", ct).ConfigureAwait(false);
        }
    }

    private sealed record LegacyDeleteResult(int Deleted, int Failed);

    private static async Task<ArrMediaFileMatch?> FindMediaFileWithRetryAsync(
        ArrClient client, string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await client.FindMediaFileAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 4 && !ct.IsCancellationRequested
                && exception is IOException or HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WaitForTerminalAndSaveReportAsync(
        HttpClient http,
        Uri failuresUri,
        string packageDigest,
        string reportPath,
        CancellationToken ct)
    {
        while (true)
        {
            using var response = await http.GetAsync(failuresUri, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                continue;
            }
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var current = JsonSerializer.Deserialize<FailedImportReport>(body, JsonOptions)
                ?? throw new InvalidDataException("Terminal failure report is empty.");
            if (!current.Status || current.PackageDigest != packageDigest)
                throw new InvalidDataException("Terminal failure report belongs to a different package.");
            if (!File.Exists(reportPath))
            {
                await WritePrivateFileAsync(reportPath, body, ct).ConfigureAwait(false);
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                    .ToLowerInvariant();
                await WritePrivateFileAsync(reportPath + ".sha256",
                    $"{digest}  {Path.GetFileName(reportPath)}\n", ct).ConfigureAwait(false);
            }
            return;
        }
    }

    private static async Task<T> ReadPrivateJsonAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false)
               ?? throw new InvalidDataException($"JSON file '{path}' is empty.");
    }

    private static async Task SetStageAsync(
        string path,
        FailedImportCleanupJournal journal,
        FailedImportCleanupEntry entry,
        string stage,
        CancellationToken ct)
    {
        var index = journal.Entries.FindIndex(item => item.DavItemId == entry.DavItemId);
        journal.Entries[index] = entry with { Stage = stage };
        await SaveJournalAsync(path, journal, ct).ConfigureAwait(false);
    }

    private static async Task SaveJournalAsync(
        string path,
        FailedImportCleanupJournal journal,
        CancellationToken ct)
    {
        await WritePrivateFileAsync(path, JsonSerializer.Serialize(journal, JsonOptions) + "\n", ct)
            .ConfigureAwait(false);
    }

    private static async Task WritePrivateFileAsync(string path, string contents, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), ct).ConfigureAwait(false);
            // The journal stage must reach disk before the external delete/search.
#pragma warning disable CA1849
            stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
        }
        File.Move(temp, path, overwrite: true);
    }
}
