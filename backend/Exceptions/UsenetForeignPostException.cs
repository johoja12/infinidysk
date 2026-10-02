namespace NzbWebDAV.Exceptions;

/// <summary>
/// A conclusive miss for a whole file: enough of its articles came back as yEnc data from
/// a different post, with none matching, that reading on can only gap-fill. Carries the
/// file's first article id so existing missing-article handling (fail-fast cache, repair)
/// can key on it.
/// </summary>
internal sealed class UsenetForeignPostException(
    string segmentId,
    int foreignArticles,
    int expectedTotalParts)
    : UsenetArticleNotFoundException(
        segmentId,
        $"{foreignArticles} articles of this {expectedTotalParts}-part file returned yEnc data " +
        "from a different post and no provider returned the file's own post.")
{
    public int ForeignArticles => foreignArticles;
}
