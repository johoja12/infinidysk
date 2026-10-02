using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.FileAggregators;

/// <summary>
/// Pure projection of the files a completed import will mount, computed from processor
/// results before the database is touched. Mirrors each aggregator's output naming so
/// pre-commit filtering (import-readiness target selection) applies the same sample
/// size heuristic the blocklist post-processor applies to persisted items. Duplicate
/// renames preserve extensions, so planned and persisted video sets always match.
/// </summary>
internal static class PlannedImportOutputs
{
    /// <summary>
    /// One planned mounted output: its name, published size, the release-relative path the
    /// mount will use, and the posted yEnc files whose articles carry its bytes.
    /// </summary>
    internal sealed record PlannedOutput(
        string Name,
        long FileSize,
        string RelativePath,
        IReadOnlyList<PostedArticleFile> SourceFiles);

    internal static long GetLargestVideoFileSize(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        return PlanOutputs(processorResults, mountName)
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .Select(x => x.FileSize)
            .DefaultIfEmpty(0)
            .Max();
    }

    internal static IEnumerable<PlannedOutput> PlanOutputs(
        List<BaseProcessor.Result> processorResults,
        string mountName)
    {
        foreach (var direct in FileAggregator.PlanDirectFiles(processorResults, mountName))
            yield return new PlannedOutput(direct.Name, direct.FileSize, direct.RelativePath, [ToPostedFile(direct.NzbFile)]);

        var rarGroups = processorResults
            .OfType<RarProcessor.Result>()
            .SelectMany(x => x.StoredFileSegments)
                        .GroupBy(x => (x.ArchiveSetId, x.PathWithinArchive))
            .ToList();
        foreach (var group in rarGroups)
        {
            var parts = group.ToList();
            var sniffedVideoExtension = parts
                .Select(x => x.SniffedVideoExtension)
                .FirstOrDefault(x => x is not null);
            var name = ImportableVideoNamer.Normalize(
                PathSanitizer.SanitizeComponent(Path.GetFileName(group.Key.PathWithinArchive)),
                sniffedVideoExtension,
                mountName,
                allowBaseRename: rarGroups.Count == 1);
            yield return new PlannedOutput(
                name,
                RarAggregator.ResolvePublishedFileSize(parts),
                name,
                parts.Select(x => x.NzbFile).Distinct().Select(ToPostedFile).ToList());
        }

        foreach (var lazy in processorResults.OfType<LazyRarProcessor.Result>())
        {
            var name = ImportableVideoNamer.Normalize(
                PathSanitizer.SanitizeComponent(Path.GetFileName(lazy.PathInArchive)),
                lazy.SniffedVideoExtension,
                mountName,
                allowBaseRename: true);
            var sources = new List<PostedArticleFile> { ToPostedFile(lazy.FirstPart) };
            sources.AddRange(lazy.PendingParts.Select(part =>
                new PostedArticleFile(part.SegmentIds, part.SegmentFallbackIds)));
            yield return new PlannedOutput(name, lazy.TotalFileSize, name, sources);
        }

        foreach (var result in processorResults.OfType<SevenZipProcessor.Result>())
        {
            foreach (var sevenZipGroup in result.SevenZipFiles.GroupBy(x => x.ArchiveSetId, StringComparer.Ordinal))
            {
                var sevenZipFiles = sevenZipGroup.ToList();
                foreach (var sevenZipFile in sevenZipFiles)
                {
                    var meta = sevenZipFile.DavMultipartFileMeta;
                    var name = ImportableVideoNamer.Normalize(
                        PathSanitizer.SanitizeComponent(Path.GetFileName(sevenZipFile.PathWithinArchive)),
                        sevenZipFile.SniffedVideoExtension,
                        mountName,
                        allowBaseRename: sevenZipFiles.Count == 1);
                    yield return new PlannedOutput(
                        name,
                        meta.AesParams?.DecodedSize
                            ?? meta.FileParts.Sum(x => x.FilePartByteRange.Count),
                        name,
                        meta.FileParts.Select(ToPostedFile).ToList());
                }
            }
        }

        foreach (var multipart in processorResults.OfType<MultipartMkvProcessor.Result>())
        {
            var name = PathSanitizer.SanitizeComponent(multipart.Filename);
            yield return new PlannedOutput(
                name,
                multipart.Parts.Sum(x => x.FilePartByteRange.Count),
                name,
                multipart.Parts.Select(ToPostedFile).ToList());
        }
    }

    private static PostedArticleFile ToPostedFile(NzbFile nzbFile)
    {
        var rangeIndex = nzbFile.GetSegmentByteRangeIndex();
        return new PostedArticleFile(
            nzbFile.GetSegmentIds(),
            nzbFile.GetSegmentFallbackIds(),
            rangeIndex.IsTrusted ? rangeIndex.Ranges : null);
    }

    private static PostedArticleFile ToPostedFile(DavMultipartFile.FilePart part) =>
        new(
            part.SegmentIds,
            part.SegmentFallbackIds,
            part.SegmentByteRangesTrusted == true ? part.SegmentByteRanges : null);
}
