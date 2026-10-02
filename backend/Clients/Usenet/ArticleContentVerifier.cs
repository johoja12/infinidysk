using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// One posted yEnc file: its article ids in part order (yEnc parts 1..N), optional sibling
/// fallback ids per article, and the exact byte range of each article when it is known.
/// </summary>
internal sealed record PostedArticleFile(
    string[] SegmentIds,
    string[][]? SegmentFallbackIds = null,
    LongRange[]? TrustedSegmentRanges = null);

/// <summary>An imported file and the posted yEnc files whose articles carry its bytes.</summary>
internal sealed record ArticleContentTarget(string Name, IReadOnlyList<PostedArticleFile> Files)
{
    public int SegmentCount => Files.Sum(file => file.SegmentIds.Length);
}

/// <summary>How many articles a verification samples: a fraction of the file, clamped.</summary>
internal readonly record struct ArticleContentSampleBudget(int Floor, int Cap, double Fraction)
{
    /// <summary>Import and the one-time library sweep: first, last and ~1% spread, 8–32 articles.</summary>
    public static ArticleContentSampleBudget Import { get; } = new(8, 32, 0.01);

    /// <summary>Recurring health checks: a lighter 4–12 article spread on top of the STAT sweep.</summary>
    public static ArticleContentSampleBudget HealthCheck { get; } = new(4, 12, 0.005);

    public int TargetFor(int segmentCount)
    {
        if (segmentCount <= 0) return 0;
        var proportional = (int)Math.Min(int.MaxValue, Math.Ceiling(segmentCount * Fraction));
        return Math.Min(segmentCount, Math.Clamp(proportional, Floor, Math.Max(Floor, Cap)));
    }
}

/// <summary>The result of sampling one imported file's articles.</summary>
internal sealed record ArticleContentVerification(
    string Name,
    int Sampled,
    IReadOnlyList<string> ForeignSegmentIds,
    IReadOnlyList<string> MissingSegmentIds,
    int Inconclusive,
    string? FirstMismatch)
{
    /// <summary>At least one sampled article belongs to a different post.</summary>
    public bool HasForeignArticles => ForeignSegmentIds.Count > 0;

    /// <summary>Foreign or definitively missing sampled articles: the file cannot be served intact.</summary>
    public bool IsDamaged => ForeignSegmentIds.Count > 0 || MissingSegmentIds.Count > 0;

    public IReadOnlyList<string> DamagedSegmentIds =>
        ForeignSegmentIds.Concat(MissingSegmentIds).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Operator-facing summary, e.g. for a SAB fail_message or a health result.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (ForeignSegmentIds.Count > 0)
            parts.Add($"{ForeignSegmentIds.Count} of {Sampled} sampled articles belong to a different post");
        if (MissingSegmentIds.Count > 0)
            parts.Add($"{MissingSegmentIds.Count} of {Sampled} sampled articles are missing on every provider");
        if (parts.Count == 0)
            return $"{Sampled} sampled articles match this file";
        var detail = FirstMismatch is null ? "" : $" (e.g. {FirstMismatch})";
        return string.Join("; ", parts) + detail;
    }
}

/// <summary>
/// Reads a bounded sample of an imported file's article bodies far enough to parse the yEnc
/// header, and checks each belongs to this file: <c>part</c> equals the segment position,
/// <c>total</c> equals the posted file's article count, the <c>=ypart</c> range matches the
/// known segment range (or the file's geometry when ranges are not known), and every sampled
/// article reports the same <c>=ybegin size</c>. STAT only proves that <em>an</em> article
/// exists for a message-id; when posting tools reuse ids, the article served is another post's.
/// Provider walks run under <see cref="YencFileValidationContext"/>, so a provider that returns
/// a foreign body is skipped in favour of one that holds the file's own article.
/// </summary>
internal sealed class ArticleContentVerifier(INntpClient usenetClient)
{
    internal const int DefaultConcurrency = 4;

    public async Task<ArticleContentVerification> VerifyAsync(
        ArticleContentTarget target,
        ArticleContentSampleBudget budget,
        int concurrency,
        CancellationToken ct,
        IReadOnlyCollection<string>? extraSegmentIds = null,
        Func<string, bool>? isExcluded = null)
    {
        var samples = SelectSamples(target, budget, extraSegmentIds, isExcluded);
        var results = new SampleResult[samples.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        // Contexts ride on the caller's token (CancellationTokenContext is token-keyed), so the
        // samples must be awaited on that same token rather than a Parallel.ForEachAsync token.
        var tasks = samples.Select(async (sample, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                results[index] = await VerifySampleAsync(target, sample, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        FlagInconsistentFileSizes(samples, results);

        var foreign = new List<string>();
        var missing = new List<string>();
        var inconclusive = 0;
        string? firstMismatch = null;
        for (var index = 0; index < results.Length; index++)
        {
            var result = results[index];
            switch (result.Outcome)
            {
                case SampleOutcome.Foreign:
                    foreign.Add(result.SegmentId);
                    firstMismatch ??= result.Reason;
                    break;
                case SampleOutcome.Missing:
                    missing.Add(result.SegmentId);
                    break;
                case SampleOutcome.Inconclusive:
                    inconclusive++;
                    break;
            }
        }

        return new ArticleContentVerification(target.Name, samples.Count, foreign, missing, inconclusive, firstMismatch);
    }

    /// <summary>
    /// Picks the first and last article and an even spread between them over the whole
    /// target (across all posted files), plus any explicitly requested article ids.
    /// </summary>
    internal static IReadOnlyList<SampleLocation> SelectSamples(
        ArticleContentTarget target,
        ArticleContentSampleBudget budget,
        IReadOnlyCollection<string>? extraSegmentIds = null,
        Func<string, bool>? isExcluded = null)
    {
        var total = target.SegmentCount;
        var globalIndexes = new SortedSet<int>();
        var count = budget.TargetFor(total);
        if (count >= total)
        {
            for (var index = 0; index < total; index++) globalIndexes.Add(index);
        }
        else if (count > 0)
        {
            globalIndexes.Add(0);
            globalIndexes.Add(total - 1);
            for (var step = 1; step < count - 1; step++)
                globalIndexes.Add((int)Math.Round((double)step * (total - 1) / (count - 1)));
        }

        var extras = extraSegmentIds is { Count: > 0 }
            ? new HashSet<string>(extraSegmentIds, StringComparer.Ordinal)
            : null;
        var samples = new List<SampleLocation>(globalIndexes.Count + (extras?.Count ?? 0));
        var partStart = 0;
        using var next = globalIndexes.GetEnumerator();
        var hasNext = next.MoveNext();
        for (var fileIndex = 0; fileIndex < target.Files.Count; fileIndex++)
        {
            var ids = target.Files[fileIndex].SegmentIds;
            var partEnd = partStart + ids.Length;
            for (var local = 0; local < ids.Length; local++)
            {
                var global = partStart + local;
                var selected = false;
                while (hasNext && next.Current < global) hasNext = next.MoveNext();
                if (hasNext && next.Current == global)
                {
                    selected = true;
                    hasNext = next.MoveNext();
                }

                if (!selected && extras is not null && extras.Contains(ids[local]))
                    selected = true;
                // Articles already accounted for (a recorded hole, a PAR2 patch served
                // locally) say nothing new about the post and are not re-read.
                if (selected && isExcluded?.Invoke(ids[local]) != true)
                    samples.Add(new SampleLocation(fileIndex, local));
            }

            partStart = partEnd;
        }

        return samples;
    }

    private async Task<SampleResult> VerifySampleAsync(
        ArticleContentTarget target,
        SampleLocation sample,
        CancellationToken ct)
    {
        var file = target.Files[sample.FileIndex];
        var primaryId = file.SegmentIds[sample.LocalIndex];
        var primary = await ProbeAsync(target.Name, file, sample.LocalIndex, primaryId, ct).ConfigureAwait(false);
        if (primary.Outcome is SampleOutcome.Matched or SampleOutcome.Inconclusive)
            return primary;

        // A sibling NZB's copy of the same article serves playback just as well.
        if (file.SegmentFallbackIds is { } fallbacks
            && sample.LocalIndex < fallbacks.Length
            && fallbacks[sample.LocalIndex] is { Length: > 0 } alternates)
        {
            foreach (var alternate in alternates)
            {
                var result = await ProbeAsync(target.Name, file, sample.LocalIndex, alternate, ct).ConfigureAwait(false);
                if (result.Outcome == SampleOutcome.Matched)
                    return result with { SegmentId = primaryId };
            }
        }

        return primary;
    }

    private async Task<SampleResult> ProbeAsync(
        string name,
        PostedArticleFile file,
        int localIndex,
        string segmentId,
        CancellationToken ct)
    {
        var position = localIndex + 1;
        UsenetYencHeader header;
        using var validation = YencFileValidationContext.BeginContentVerification(
            file.SegmentIds, file.SegmentFallbackIds, name);
        try
        {
            header = await usenetClient.GetYencHeadersAsync(segmentId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e.TryGetCausingException<UsenetMismatchedArticleException>(out var mismatch)
                                  && mismatch is not null)
        {
            return new SampleResult(segmentId, SampleOutcome.Foreign, $"segment {position}: {mismatch.Message}", null);
        }
        catch (Exception e) when (e.TryGetCausingException<UsenetForeignPostException>(out var foreign)
                                  && foreign is not null)
        {
            return new SampleResult(segmentId, SampleOutcome.Foreign, $"segment {position}: {foreign.Message}", null);
        }
        catch (Exception e) when (e.TryGetCausingException<UsenetArticleNotFoundException>(out var notFound)
                                  && notFound is not null)
        {
            // A provider answered with another post's article and the rest had nothing: the
            // walk reports the last (missing) outcome, but the article is still foreign.
            if (validation.ReportedMismatches > 0)
                return new SampleResult(segmentId, SampleOutcome.Foreign,
                    $"segment {position}: a provider returned an article from a different post", null);
            return notFound.InconclusiveReason is null
                ? new SampleResult(segmentId, SampleOutcome.Missing, $"segment {position} is missing", null)
                : new SampleResult(segmentId, SampleOutcome.Inconclusive, notFound.InconclusiveReason, null);
        }
        catch (NonRetryableDownloadException e) when (e.Message.Contains("not yEnc-encoded", StringComparison.Ordinal))
        {
            return new SampleResult(segmentId, SampleOutcome.Foreign, $"segment {position} is not a yEnc article", null);
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            e.TryGetKnownErrorMessage(out var reason);
            return new SampleResult(segmentId, SampleOutcome.Inconclusive, reason, null);
        }

        var trustedRange = file.TrustedSegmentRanges is { } ranges && localIndex < ranges.Length
            ? ranges[localIndex]
            : null;
        var mismatchReason = DescribeHeaderMismatch(header, position, file.SegmentIds.Length, trustedRange);
        return mismatchReason is null
            ? new SampleResult(segmentId, SampleOutcome.Matched, null, header)
            : new SampleResult(segmentId, SampleOutcome.Foreign, mismatchReason, header);
    }

    /// <summary>
    /// Null when the yEnc header can belong to segment <paramref name="position"/> of a posted
    /// file with <paramref name="expectedTotalParts"/> articles; otherwise why it cannot.
    /// </summary>
    internal static string? DescribeHeaderMismatch(
        UsenetYencHeader header,
        int position,
        int expectedTotalParts,
        LongRange? trustedRange = null)
    {
        if (!YencFileValidationContext.MatchesTotal(header, expectedTotalParts, position))
            return $"segment {position}: yEnc part {header.PartNumber}/{header.TotalParts} " +
                   $"for a {expectedTotalParts}-part file";

        // Single-part yEnc (no =ypart line) carries no position to compare.
        if (header.PartNumber <= 0) return null;

        if (header.PartNumber != position)
            return $"segment {position}: yEnc part {header.PartNumber}/{header.TotalParts}";

        if (trustedRange is { } range)
        {
            return header.PartOffset == range.StartInclusive && header.PartSize == range.Count
                ? null
                : $"segment {position}: yEnc range {header.PartOffset + 1}-{header.PartOffset + header.PartSize} " +
                  $"instead of {range.StartInclusive + 1}-{range.EndExclusive}";
        }

        // Without recorded ranges, the part's range must still be possible for its position.
        var plausible = position == 1
            ? header.PartOffset == 0
            : header.PartOffset > 0
              && (header.FileSize <= 0 || header.PartOffset + header.PartSize <= header.FileSize);
        return plausible
            ? null
            : $"segment {position}: yEnc range {header.PartOffset + 1}-{header.PartOffset + header.PartSize} " +
              $"is impossible for part {position}";
    }

    /// <summary>
    /// Every article of one post reports the same <c>=ybegin size</c>. Within each posted
    /// file, sampled articles whose size disagrees with the majority belong to another post
    /// even when part and total happen to line up.
    /// </summary>
    private static void FlagInconsistentFileSizes(IReadOnlyList<SampleLocation> samples, SampleResult[] results)
    {
        foreach (var group in samples
                     .Select((sample, index) => (sample.FileIndex, Index: index))
                     // Only multi-part yEnc articles (with =ypart) describe one shared file size.
                     .Where(entry => results[entry.Index] is
                         { Outcome: SampleOutcome.Matched, Header: { FileSize: > 0, PartNumber: > 0 } })
                     .GroupBy(entry => entry.FileIndex))
        {
            var sizes = group
                .GroupBy(entry => results[entry.Index].Header!.FileSize)
                .OrderByDescending(sizeGroup => sizeGroup.Count())
                .ToList();
            if (sizes.Count < 2 || sizes[0].Count() == sizes[1].Count()) continue;
            var expectedSize = sizes[0].Key;
            foreach (var minority in sizes.Skip(1))
            {
                foreach (var entry in minority)
                {
                    var result = results[entry.Index];
                    results[entry.Index] = result with
                    {
                        Outcome = SampleOutcome.Foreign,
                        Reason = $"segment {samples[entry.Index].LocalIndex + 1}: yEnc size {minority.Key} " +
                                 $"instead of {expectedSize}",
                    };
                }
            }
        }
    }

    internal readonly record struct SampleLocation(int FileIndex, int LocalIndex);

    private enum SampleOutcome
    {
        Matched,
        Foreign,
        Missing,
        Inconclusive,
    }

    private sealed record SampleResult(
        string SegmentId,
        SampleOutcome Outcome,
        string? Reason,
        UsenetYencHeader? Header);
}
