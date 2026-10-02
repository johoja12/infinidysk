namespace NzbWebDAV.Exceptions;

/// <summary>
/// Sampled articles of an imported file belong to a different post (message-id collision)
/// or are missing even though STAT found them. Derives from
/// <see cref="UsenetArticleNotFoundException"/> so the health check's existing missing-article
/// repair routing (PAR2 first when enabled, otherwise the Arr replacement) handles it, with
/// every damaged article id treated as missing.
/// </summary>
internal sealed class UsenetContentMismatchException(IReadOnlyList<string> damagedSegmentIds, string detail)
    : UsenetArticleNotFoundException(
        damagedSegmentIds.Count > 0
            ? damagedSegmentIds[0]
            : throw new ArgumentException("At least one damaged article id is required.", nameof(damagedSegmentIds)),
        detail)
{
    public IReadOnlyList<string> DamagedSegmentIds => damagedSegmentIds;
}
