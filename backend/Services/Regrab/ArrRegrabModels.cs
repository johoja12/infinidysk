using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services.Regrab;

/// <summary>The Sonarr/Radarr media file a regrab acts on.</summary>
public sealed record ArrRegrabTarget(
    string App,
    string Host,
    string MediaKind,
    int FileId,
    IReadOnlyList<int> MediaIds,
    string LibraryPath)
{
    public string Label => $"{App} {MediaKind} {string.Join(",", MediaIds)} on {DisplayHost(Host)}";

    internal ArrMediaFileMatch ToMatch() => new(
        MediaKind == "movie" ? ArrMediaKind.Movie : ArrMediaKind.Episode,
        FileId,
        MediaIds);

    internal static string DisplayHost(string host) =>
        Uri.TryCreate(host, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped)
            : host;
}

public sealed record ArrRegrabRequestView(
    Guid Id,
    string Status,
    string Source,
    string ReleaseName,
    string? LibraryPath,
    string? ArrApp,
    string? ArrHost,
    string? ArrMediaKind,
    string? Reason,
    string? Message,
    bool LinkRemoved,
    bool Blocklisted,
    int Attempts,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? RequestedAt)
{
    internal static ArrRegrabRequestView From(ArrRegrabRequest row) => new(
        row.Id,
        row.Status,
        row.Source,
        row.ReleaseName,
        row.LibraryPath,
        row.ArrMediaKind is null ? null : row.ArrMediaKind == "movie" ? "Radarr" : "Sonarr",
        row.ArrHost is null ? null : ArrRegrabTarget.DisplayHost(row.ArrHost),
        row.ArrMediaKind,
        row.Reason,
        row.LastError,
        row.LinkRemoved,
        row.Blocklisted,
        row.Attempts,
        row.CreatedAt,
        row.UpdatedAt,
        row.RequestedAt);
}

public sealed record ArrRegrabPreview(
    bool Eligible,
    string? DisabledReason,
    string ReleaseName,
    string? LibraryPath,
    bool OldLibraryLink,
    ArrRegrabTarget? Target,
    ArrRegrabRequestView? Request);

public sealed record ArrRegrabResult(
    bool Accepted,
    string Message,
    ArrRegrabRequestView? Request);

/// <summary>One failed migration import, as reported by <c>GET /api/migration/nzbdav/import-failures</c>.</summary>
public sealed record MigrationFailureRecord
{
    public string? LegacyDavItemId { get; init; }
    public string? LibraryRelativePath { get; init; }
    public string? Reason { get; init; }
    public string? SubmissionState { get; init; }
    public string? SourceReleaseId { get; init; }
    public string? OriginalTarget { get; init; }
    public string? ReleaseName { get; init; }
    public int? BatchIndex { get; init; }
}

public sealed record MigrationRegrabCandidate(
    MigrationFailureRecord Failure,
    string Outcome,
    string? LibraryPath,
    string Message);

public sealed record MigrationRegrabPreview(
    int Total,
    int Eligible,
    int AlreadyRequested,
    IReadOnlyDictionary<string, int> Skipped,
    IReadOnlyList<MigrationRegrabCandidate> Candidates,
    string PreviewToken);

public static class MigrationRegrabOutcome
{
    public const string Eligible = "eligible";
    public const string AlreadyRequested = "already-requested";
    public const string NotEligibleReason = "not-damaged-or-missing-articles";
    public const string NotFailed = "not-a-confirmed-failure";
    public const string InvalidRecord = "invalid-record";
    public const string SourceLinkMissing = "source-link-missing";
    public const string SourceLinkChanged = "source-link-changed";
    public const string NotSymlink = "not-a-symlink";
    public const string OutsideRoots = "outside-library-roots";
    public const string Ambiguous = "ambiguous-library-root";
}
