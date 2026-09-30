namespace NzbWebDAV.Exceptions;

/// <summary>
/// Thrown when the media source behind an in-flight response is replaced (for example a
/// repair or re-import swapped the file). The client must retry the range against the
/// current source; this is an expected operational condition, not data loss.
/// </summary>
public sealed class MediaSourceChangedException(string message) : IOException(message);
