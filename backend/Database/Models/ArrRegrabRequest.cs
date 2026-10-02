namespace NzbWebDAV.Database.Models;

/// <summary>
/// One durable "get another copy of this file" request routed through Sonarr/Radarr.
/// Created by the file-modal Regrab action, by failed migration imports, and recorded
/// by health repair when it removes and blocklists a release. <see cref="DedupKey"/>
/// keeps one row per library item so retries and repeated clicks are idempotent.
/// </summary>
public class ArrRegrabRequest
{
    public Guid Id { get; set; }

    /// <summary><c>dav:{id}</c>, <c>link:{absolute path}</c>, or <c>migration:{legacy id}</c>.</summary>
    public string DedupKey { get; set; } = null!;

    /// <summary>manual | migration | health-repair</summary>
    public string Source { get; set; } = null!;

    /// <summary>See <see cref="ArrRegrabStatus"/>.</summary>
    public string Status { get; set; } = null!;

    public Guid? DavItemId { get; set; }

    /// <summary>Absolute library link path handed to Sonarr/Radarr.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>
    /// For migration rows: the symlink must still point at this legacy target
    /// (an exact target, or a <c>.ids</c> target ending with the legacy DavItem id).
    /// </summary>
    public string? ExpectedLinkTarget { get; set; }

    public string ReleaseName { get; set; } = null!;
    public string? Reason { get; set; }

    public string? ArrHost { get; set; }

    /// <summary>movie | episode</summary>
    public string? ArrMediaKind { get; set; }

    public int? ArrFileId { get; set; }

    /// <summary>Comma-separated Arr movie or episode ids covered by the search.</summary>
    public string? ArrMediaIds { get; set; }

    public bool LinkRemoved { get; set; }
    public string? PreviousLinkTarget { get; set; }
    public bool ArrFileRemoved { get; set; }
    public bool Blocklisted { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public int? MigrationBatchIndex { get; set; }
    public string? MigrationSourceReleaseId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? RequestedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class ArrRegrabStatus
{
    /// <summary>Waiting for (another) attempt by the regrab worker.</summary>
    public const string Pending = "pending";

    /// <summary>The Arr file record was removed and a replacement search was requested.</summary>
    public const string Requested = "requested";

    /// <summary>The Arr file record was removed, but the per-media search budget withheld the search.</summary>
    public const string SearchWithheld = "search-withheld";

    /// <summary>A new file replaced the regrabbed one.</summary>
    public const string Replaced = "replaced";

    /// <summary>Not attempted because a safety guard did not hold (link changed, no Arr mapping, ...).</summary>
    public const string Skipped = "skipped";

    /// <summary>Attempts were exhausted or a non-retryable error occurred.</summary>
    public const string Failed = "failed";

    public static bool IsActive(string status) =>
        status is Pending or Requested or SearchWithheld;
}
