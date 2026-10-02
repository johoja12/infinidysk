using System.Diagnostics;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue.FileAggregators;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Queue.PostProcessors;

/// <summary>
/// Import-time article content verification (#130). For each media file the import will
/// mount, samples its articles' yEnc headers and fails the import when any sampled article
/// belongs to a different post — message-id collisions that STAT, the existence checks and
/// the head/tail readiness probe cannot see. The SAB history fail message lets Sonarr/Radarr
/// blocklist the release and grab another. PAR2 sets, samples, NFOs and other files that
/// are not mounted as library media are never sampled.
/// </summary>
internal sealed class ImportArticleContentValidator(INntpClient usenetClient)
{
    internal const string FailurePrefix = "Release damaged on Usenet:";

    /// <summary>
    /// The posted files behind every media output the import will mount, minus the outputs
    /// output filtering removes (blocklist globs and the sample heuristic).
    /// </summary>
    internal static IReadOnlyList<ArticleContentTarget> PlanTargets(
        List<BaseProcessor.Result> processorResults,
        string category,
        string mountName,
        ConfigManager configManager)
    {
        var largestVideoFileSize = PlannedImportOutputs.GetLargestVideoFileSize(processorResults, mountName);
        var blocklistedFilenames = configManager.GetBlocklistedFiles();
        var sampleFilterEnabled = configManager.IsSampleFilterEnabled();

        var targets = new List<ArticleContentTarget>();
        foreach (var output in PlannedImportOutputs.PlanOutputs(processorResults, mountName))
        {
            if (!FilenameUtil.IsMediaFile(output.Name)) continue;
            if (FileFilterUtil.GetRemovalReason(
                    output.Name,
                    output.FileSize,
                    PlannedDavPath(category, mountName, output.RelativePath, output.Name),
                    largestVideoFileSize,
                    blocklistedFilenames,
                    sampleFilterEnabled) is not null)
                continue;

            var target = new ArticleContentTarget(output.Name, output.SourceFiles);
            if (target.SegmentCount > 0) targets.Add(target);
        }

        return targets;
    }

    /// <summary>
    /// Verifies each target in turn and throws a non-retryable failure at the first file
    /// with foreign articles. Missing or unreadable samples are reported, not failed: article
    /// existence policy belongs to the configured existence check and readiness probe.
    /// </summary>
    public async Task ValidateAsync(
        IReadOnlyList<ArticleContentTarget> targets,
        ArticleContentSampleBudget budget,
        int concurrency,
        string jobName,
        CancellationToken ct)
    {
        var verifier = new ArticleContentVerifier(usenetClient);
        foreach (var target in targets)
        {
            var timer = Stopwatch.StartNew();
            var result = await verifier.VerifyAsync(target, budget, concurrency, ct).ConfigureAwait(false);
            if (result.HasForeignArticles)
            {
                Log.Warning(
                    "Import of {JobName} rejected: {FileName} is damaged on Usenet. {Detail}. " +
                    "Its message-ids resolve to another upload's articles, so Sonarr/Radarr can blocklist " +
                    "this release and grab another.",
                    jobName, target.Name, result.Describe());
                throw new NonRetryableDownloadException(FormatFailureMessage(result));
            }

            if (result.Inconclusive == result.Sampled && result.Sampled > 0)
            {
                Log.Warning(
                    "Could not verify article content of {FileName} for {JobName}: none of {Sampled} sampled " +
                    "articles could be read. Importing without content verification.",
                    target.Name, jobName, result.Sampled);
                continue;
            }

            Log.Information(
                "Verified article content of {FileName}: {Detail}; missing {Missing}, unreadable {Inconclusive} " +
                "({ElapsedMs} ms).",
                target.Name, result.Describe(), result.MissingSegmentIds.Count, result.Inconclusive,
                timer.ElapsedMilliseconds);
        }
    }

    internal static string FormatFailureMessage(ArticleContentVerification result) =>
        $"{FailurePrefix} {result.ForeignSegmentIds.Count} of {result.Sampled} sampled articles " +
        $"belong to a different post ({result.Name}" +
        (result.FirstMismatch is null ? ")." : $"; e.g. {result.FirstMismatch}).");

    private static string PlannedDavPath(string category, string mountName, string relativePath, string name)
    {
        // Mirrors the mounted path shape (/content/<category>/<job>/<dirs>/<name>) so the
        // sample-directory heuristic sees the same release subfolders.
        var segments = relativePath
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'],
                StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Select(segment => PathSanitizer.SanitizeComponent(segment));
        return string.Join(
            '/',
            new[] { DavItem.ContentFolder.Path, category, mountName }.Concat(segments).Append(name));
    }
}
