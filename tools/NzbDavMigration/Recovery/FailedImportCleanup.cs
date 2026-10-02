using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbDavMigration.Canary;
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
    string Stage,
    ArrCleanupTarget? ArrTarget = null,
    ArrCleanupState? ArrState = null);

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
        bool skipChangedCurrentSources = false,
        string? validationPlanPath = null,
        string? validationJournalPath = null,
        string? validationResultsPath = null,
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
        var validation = validationPlanPath is not null || validationJournalPath is not null
            || validationResultsPath is not null;
        if (validation && (historical || waitForTerminal || validationPlanPath is null
                           || validationJournalPath is null || validationResultsPath is null))
            throw new InvalidDataException("Validation cleanup requires a plan, apply journal, and validation results without historical or terminal-report mode.");
        if (historical && (historicalCorrelationPath is null
                           || historicalAcknowledgementPath is null || waitForTerminal))
            throw new InvalidDataException("Historical cleanup needs both archived correlation and acknowledgement, without waiting for a current batch.");
        if (skipChangedHistoricalSources && !historical)
            throw new InvalidDataException("Changed-source skipping is only available for archived batches.");
        if (skipChangedCurrentSources && (historical || validation))
            throw new InvalidDataException("Current changed-source skipping requires a terminal import batch.");
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

        if (validation)
        {
            await AssertValidationFailuresAsync(package, report, sourceRoot, validationPlanPath!,
                validationJournalPath!, validationResultsPath!, ct).ConfigureAwait(false);
        }
        else if (historical)
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
        foreach (var item in journal.Entries.ToArray())
        {
            if (item.Stage is "completed" or "skipped_source_changed") continue;
            if (item.Stage == "arr_cleanup_started" && item.ArrState?.Stage == "completed"
                && item.ArrTarget is not null)
            {
                // The accepted search was saved before a crash between the two journal writes.
                await SetStageAsync(journalPath, journal, item, "completed", ct).ConfigureAwait(false);
                continue;
            }
            if (item.Stage == "arr_cleanup_started" && (item.ArrTarget is null || item.ArrState is null))
                throw new InvalidOperationException(
                    $"Cleanup of {item.DavItemId} has an uncertain external outcome; reconcile it before resuming.");
            if (item.Stage is not ("planned" or "source_deleting" or "source_deleted" or "arr_cleanup_started"))
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
                catch (InvalidDataException) when (skipChangedHistoricalSources || skipChangedCurrentSources)
                {
                    await SetStageAsync(journalPath, journal, item, "skipped_source_changed", ct)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            var arrPath = Path.Join(canonicalArrRoot, row.LibraryRelativePath);
            if (item.ArrTarget is not null)
            {
                if (item.ArrTarget.Path != arrPath)
                    throw new InvalidDataException("Persisted Arr target path disagrees with the selected link.");
                var client = clients.SingleOrDefault(client => InstanceKey(client) == item.ArrTarget.InstanceKey)
                    ?? throw new InvalidDataException("Persisted Arr cleanup instance is no longer enabled.");
                if ((client is SonarrClient) != (item.ArrTarget.Match.Kind == ArrMediaKind.Episode))
                    throw new InvalidDataException("Persisted Arr cleanup kind disagrees with its instance.");
                actions.Add((item, client, item.ArrTarget.Match));
                continue;
            }
            var matches = new List<(ArrClient Client, ArrMediaFileMatch Match)>();
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                matches.Clear();
                foreach (var client in clients)
                {
                    var match = await FindMediaFileWithRetryAsync(client, arrPath, ct)
                        .ConfigureAwait(false);
                    if (match is not null) matches.Add((client, match));
                }
                if (matches.Count > 0 || attempt == 4) break;
                // *Arr can briefly report no file while its library index refreshes.
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
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
            if (item.ArrTarget is null)
            {
                item = item with
                {
                    ArrTarget = new ArrCleanupTarget(InstanceKey(action.Client),
                    Path.Join(canonicalArrRoot, selected[item.DavItemId].LibraryRelativePath), action.Match),
                    ArrState = new ArrCleanupState()
                };
                journal.Entries[journal.Entries.FindIndex(entry => entry.DavItemId == item.DavItemId)] = item;
                await SaveJournalAsync(journalPath, journal, ct).ConfigureAwait(false);
            }
            if (item.Stage is "planned" or "source_deleting")
            {
                var deleted = false;
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    if (item.Stage == "source_deleting")
                    {
                        try
                        {
                            await AssertSourceDeletedAsync(legacyReader, item, ct).ConfigureAwait(false);
                            if (!CanaryPathSafety.PathExistsNoFollow(item.SourcePath)) { deleted = true; break; }
                        }
                        catch (InvalidDataException) { /* Require original ownership before retry. */ }
                    }
                    try
                    {
                        await legacyReader.AssertOnlyMappedLinkAsync(item.DavItemId, item.SourcePath, ct).ConfigureAwait(false);
                        if (new FileInfo(item.SourcePath).LinkTarget != selected[item.DavItemId].OriginalTarget)
                            throw new InvalidDataException("Failed item source symlink changed during cleanup.");
                    }
                    catch (InvalidDataException) when (item.Stage == "planned"
                        && (skipChangedHistoricalSources || skipChangedCurrentSources))
                    {
                        await SetStageAsync(journalPath, journal, item, "skipped_source_changed", ct).ConfigureAwait(false);
                        break;
                    }
                    item = item with { Stage = "source_deleting" };
                    await SetStageAsync(journalPath, journal, item, item.Stage, ct).ConfigureAwait(false);
                    try
                    {
                        using var response = await http.PostAsJsonAsync(new Uri(baseUri, "api/stats/delete-files"),
                            new { davItemIds = new[] { item.DavItemId } }, ct).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        var result = await response.Content.ReadFromJsonAsync<LegacyDeleteResult>(JsonOptions, ct).ConfigureAwait(false);
                        if (result is not { Deleted: 1, Failed: 0 })
                            Console.WriteLine("Legacy deletion response needs an authoritative state recheck.");
                    }
                    catch (Exception e) when (ArrCleanupRecovery.Transient(e, ct))
                    { Console.WriteLine($"Legacy deletion needs recheck (attempt {attempt}/4)."); }
                    try
                    {
                        await AssertSourceDeletedAsync(legacyReader, item, ct).ConfigureAwait(false);
                        if (!CanaryPathSafety.PathExistsNoFollow(item.SourcePath)) { deleted = true; break; }
                    }
                    catch (InvalidDataException) when (attempt < 4) { }
                    if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct).ConfigureAwait(false);
                }
                if (journal.Entries.Single(entry => entry.DavItemId == item.DavItemId).Stage == "skipped_source_changed") continue;
                if (!deleted) throw new IOException("Legacy deletion remains pending after four verified attempts.");
                item = item with { Stage = "source_deleted" };
                await SetStageAsync(journalPath, journal, item, item.Stage, ct).ConfigureAwait(false);
            }

            await AssertSourceDeletedAsync(legacyReader, item, ct).ConfigureAwait(false);
            if (CanaryPathSafety.PathExistsNoFollow(item.SourcePath))
                throw new InvalidDataException("Source link reappeared; refusing Arr cleanup.");
            await SetStageAsync(journalPath, journal, item, "arr_cleanup_started", ct).ConfigureAwait(false);
            var details = arrConfig.GetEnabledInstances().Single(instance =>
                ArrConfig.MakeInstanceKey(instance.AppType, instance.Details.Host) == item.ArrTarget!.InstanceKey).Details;
            await ArrCleanupRecovery.RunAsync(new MigrationArrClient(details.Host, details.ApiKey),
                item.ArrTarget!, item.ArrState ?? new ArrCleanupState(), async state =>
                {
                    item = item with { Stage = "arr_cleanup_started", ArrState = state };
                    await SetStageAsync(journalPath, journal, item, item.Stage, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            await SetStageAsync(journalPath, journal, item, "completed", ct).ConfigureAwait(false);
        }
    }

    private static async Task AssertSourceDeletedAsync(LegacyNzbDavReader reader,
        FailedImportCleanupEntry item, CancellationToken ct)
    {
        await reader.AssertMappedLinkAbsentAsync(item.DavItemId, item.SourcePath, ct).ConfigureAwait(false);
        var result = await reader.ReadAsync(new[] { item.DavItemId }, ct).ConfigureAwait(false);
        if (!result.MissingIds.Contains(item.DavItemId))
            throw new InvalidDataException("Legacy deletion has not removed the recorded Dav item.");
    }

    private static string InstanceKey(ArrClient client) => ArrConfig.MakeInstanceKey(
        client is SonarrClient ? "sonarr" : "radarr", client.Host);

    private sealed record LegacyDeleteResult(int Deleted, int Failed);

    private static async Task AssertValidationFailuresAsync(
        NzbDavVerifiedPackage package, FailedImportReport report, string sourceRoot, string planPath,
        string applyJournalPath, string validationResultsPath, CancellationToken ct)
    {
        using var plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, ct).ConfigureAwait(false));
        using var journal = JsonDocument.Parse(await File.ReadAllTextAsync(applyJournalPath, ct).ConfigureAwait(false));
        using var validations = JsonDocument.Parse(await File.ReadAllTextAsync(validationResultsPath, ct).ConfigureAwait(false));
        var p = plan.RootElement;
        var j = journal.RootElement;
        var rows = validations.RootElement.EnumerateArray().ToArray();
        var planDigest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(planPath, ct)
            .ConfigureAwait(false))).ToLowerInvariant();
        if (!p.GetProperty("isValid").GetBoolean()
            || p.GetProperty("selectedCount").GetInt32() != p.GetProperty("links").GetArrayLength()
            || p.GetProperty("sourcePackageDigest").GetString() != package.PackageDigest
            || j.GetProperty("planSha256").GetString() != planDigest
            || Path.GetFullPath(j.GetProperty("planPath").GetString()!) != Path.GetFullPath(planPath)
            || j.GetProperty("sourceRoot").GetString() != Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar)
            || j.GetProperty("links").EnumerateArray().Count(item =>
                item.GetProperty("status").GetString() == "applied") != rows.Length)
            throw new InvalidDataException("Validation evidence does not match the verified package and applied plan.");
        var planned = p.GetProperty("links").EnumerateArray()
            .Where(item => item.GetProperty("applyStatus").GetString() == "planned")
            .ToDictionary(item => item.GetProperty("libraryRelativePath").GetString()!, StringComparer.Ordinal);
        var journalLinks = j.GetProperty("links").EnumerateArray().ToArray();
        var applied = journalLinks.Where(item => item.GetProperty("status").GetString() == "applied")
            .ToDictionary(item => item.GetProperty("libraryRelativePath").GetString()!, StringComparer.Ordinal);
        var missing = journalLinks.Where(item => item.GetProperty("status").GetString() == "source-missing")
            .ToDictionary(item => item.GetProperty("libraryRelativePath").GetString()!, StringComparer.Ordinal);
        var replaced = journalLinks.Where(item => item.GetProperty("status").GetString() == "source-replaced")
            .ToDictionary(item => item.GetProperty("libraryRelativePath").GetString()!, StringComparer.Ordinal);
        var byPath = rows.ToDictionary(item => item.GetProperty("libraryRelativePath").GetString()!, StringComparer.Ordinal);
        if (planned.Count != p.GetProperty("actionableCount").GetInt32()
            || planned.Count != applied.Count + missing.Count + replaced.Count || applied.Count != byPath.Count
            || journalLinks.Length != applied.Count + missing.Count + replaced.Count
            || report.Failures.Count == 0
            || report.Failures.Count != rows.Count(item => !item.GetProperty("success").GetBoolean()))
            throw new InvalidDataException("Validation results do not cover the exact applied links.");
        foreach (var (path, link) in missing)
        {
            if (!planned.ContainsKey(path)
                || CanaryPathSafety.PathExistsNoFollow(link.GetProperty("sourceLinkPath").GetString()!)
                || CanaryPathSafety.PathExistsNoFollow(link.GetProperty("linkPath").GetString()!))
                throw new InvalidDataException($"Missing source evidence changed for '{path}'.");
        }
        foreach (var (path, link) in replaced)
        {
            var sourcePath = link.GetProperty("sourceLinkPath").GetString()!;
            var replacementTarget = link.GetProperty("replacementSourceTarget").GetString();
            if (!planned.ContainsKey(path)
                || string.IsNullOrWhiteSpace(replacementTarget)
                || new FileInfo(sourcePath).LinkTarget != replacementTarget
                || CanaryPathSafety.PathExistsNoFollow(link.GetProperty("linkPath").GetString()!))
                throw new InvalidDataException($"Replaced source evidence changed for '{path}'.");
        }
        foreach (var (path, link) in applied)
        {
            if (!planned.TryGetValue(path, out var source) || !byPath.TryGetValue(path, out var result)
                || link.GetProperty("status").GetString() != "applied"
                || source.GetProperty("correlationStatus").GetString() != "exact"
                || link.GetProperty("sourceLinkPath").GetString() != Path.Join(
                    j.GetProperty("sourceRoot").GetString()!, path)
                || link.GetProperty("linkPath").GetString() != Path.Join(
                    j.GetProperty("libraryRoot").GetString()!, path)
                || link.GetProperty("observedSourceTarget").GetString() != source.GetProperty("originalLegacyTarget").GetString()
                || link.GetProperty("targetPath").GetString() != Path.Join(
                    j.GetProperty("targetRoot").GetString()!, source.GetProperty("newRelativeTarget").GetString()!)
                || link.GetProperty("expectedFileSize").GetInt64() != source.GetProperty("expectedFileSize").GetInt64())
                throw new InvalidDataException($"Validation evidence disagrees for '{path}'.");
            if (result.GetProperty("success").GetBoolean()) continue;
            var id = source.GetProperty("legacyDavItemId").GetGuid();
            if (!report.Failures.Any(item => item.LegacyDavItemId == id
                && item.LibraryRelativePath == path && item.SubmissionState == "failed"
                && !string.IsNullOrWhiteSpace(item.Reason)))
                throw new InvalidDataException($"Unrecorded validation failure for '{path}'.");
        }
        if (report.Failures.Any(item => !byPath.TryGetValue(item.LibraryRelativePath, out var result)
            || result.GetProperty("success").GetBoolean()))
            throw new InvalidDataException("Failure report contains a link that passed validation.");
    }

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
