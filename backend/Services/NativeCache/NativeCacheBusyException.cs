namespace NzbWebDAV.Services.NativeCache;

/// <summary>A warming stream found no free Native Cache buffer slot in time; the job should defer.</summary>
public sealed class NativeCacheBusyException(string message) : InvalidOperationException(message);
