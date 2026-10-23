namespace NzbWebDAV.Tests.TestUtils;

/// <summary>
/// Tests that swap the global <c>Log.Logger</c> to capture output belong to this
/// collection. xUnit runs collections in parallel, so without it two such classes
/// race on the same static and observe each other's events. Other collections
/// must also be excluded while this logger is installed: ordinary background jobs
/// can emit errors into the capture even when they do not replace the logger.
/// </summary>
[CollectionDefinition(nameof(GlobalLoggerCollection), DisableParallelization = true)]
public class GlobalLoggerCollection;
