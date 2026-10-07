import { usePageCoverage } from "~/utils/use-page-coverage";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router";
import { useIsReadOnly } from "~/auth/authorization";
import { Icon } from "~/components/ui";
import { settingsPath } from "~/navigation/settings-tabs";
import { withUrlBase } from "~/utils/url-base";
import { PredictionResults } from "./prediction-results";
import { PrefetchQueue } from "~/components/prefetch-queue";
import {
  LibraryFileModalHost,
  useLibraryFileModal,
  type LibraryFileModalController,
} from "~/components/library-file-modal/use-library-file-modal";
import {
  formatLiveWarming,
  formatSpeed,
  formatWarmingSpeed,
  medianWarmingSpeed,
} from "./warming-speed";
import { SourceBubbles, type JobSource } from "./source-bubbles";
import { decimalBytes, describeCoverage, failureReason, mebibytes } from "./job-coverage";
import {
  gapBytes,
  gapHeadline,
  gapSpan,
  isGapFill,
  missReason,
  missReasons,
  viewerLabel,
} from "./gap-fills";

type Job = {
  id: string;
  itemId: string;
  displayName?: string;
  source?: string;
  reason?: string;
  trigger: string;
  state: string;
  start: number;
  length: number;
  committedBytes: number;
  fileSize?: number;
  error?: string | null;
  updated: number;
  startedAt?: number | null;
  finishedAt?: number | null;
  activeMs?: number | null;
  warmedBytes?: number | null;
  isRangeJob?: boolean;
  rangeBytes?: number | null;
  rangeCachedBytes?: number | null;
  coveragePending?: boolean;
  failureCode?: string | null;
  remedy?: string | null;
  recentBytesPerSecond?: number | null;
  lastProgressAt?: number | null;
  stalled?: boolean | null;
  sources?: JobSource[] | null;
  sourceCount?: number | null;
  missReason?: string | null;
  viewerUser?: string | null;
  viewerPlayer?: string | null;
  mediaDurationMs?: number | null;
};
type Status = {
  available: boolean;
  healthy: boolean;
  paused: boolean;
  initializationError?: string | null;
  runtimeError?: string | null;
  lastError?: string | null;
  lastSuccess?: string | null;
  dailyBudgetUsed?: number;
  settings?: {
    enabled?: boolean;
    dailyByteBudget?: number;
    maxConcurrentJobs?: number;
    finishWatchedEnabled?: boolean;
  };
  jobs: Job[];
};
const terminal = new Set(["completed", "failed", "cancelled", "expired"]);
const filterStates = ["all", "running", "queued", "deferred", "paused", "failed"];
type HistoryKind = "warming" | "gaps";
const warmingFilters: Record<string, string> = {
  all: "All jobs",
  "fully-cached": "Fully cached",
  partial: "Partially cached",
  failed: "Failed",
  cancelled: "Cancelled",
  expired: "Expired",
};
const gapFilters: Record<string, string> = {
  all: "All reasons",
  ...Object.fromEntries(Object.entries(missReasons).map(([code, reason]) => [code, reason.label])),
  unrecorded: "Reason not recorded",
};
function matchesHistoryFilter(job: Job, kind: HistoryKind, filter: string) {
  if (filter === "all") return true;
  if (kind === "gaps")
    return filter === "unrecorded"
      ? !job.missReason || !(job.missReason in missReasons)
      : job.missReason === filter;
  if (filter === "fully-cached") return coverage(job) === 100;
  if (filter === "partial")
    return job.state === "completed" && coverage(job) !== null && coverage(job) !== 100;
  return job.state === filter;
}
const bytes = decimalBytes;
function when(value: number) {
  return Number.isFinite(value) ? new Date(value).toLocaleString() : "—";
}
function coverage(job: Job) {
  return job.fileSize && job.fileSize > 0
    ? Math.min(100, Math.max(0, Math.round((job.committedBytes / job.fileSize) * 100)))
    : null;
}
function badge(state: string) {
  const colors: Record<string, string> = {
    running: "badge-info",
    completed: "badge-success",
    failed: "badge-error",
    deferred: "badge-warning",
    paused: "badge-warning",
    queued: "badge-primary",
  };
  return <span className={`badge badge-sm ${colors[state] ?? "badge-ghost"}`}>{state}</span>;
}
function Card({
  label,
  value,
  foot,
  color = "",
}: {
  label: string;
  value: string;
  foot: string;
  color?: string;
}) {
  return (
    <div className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
      <p className="text-xs font-semibold text-base-content/65">{label}</p>
      <p className={`mt-2 text-2xl font-bold ${color}`}>{value}</p>
      <p className="mt-1 text-xs text-base-content/50">{foot}</p>
    </div>
  );
}
const reasonTone = { warning: "badge-warning", error: "badge-error", info: "badge-info" } as const;

/**
 * A backfill job: bytes playback streamed without caching. Shows where the gap was, why it was
 * missed and who was playing, on a map of the whole file so a small fill never reads as a full cache.
 */
function GapRow({ job, onOpen }: { job: Job; onOpen: (job: Job) => void }) {
  const reason = missReason(job);
  const viewer = viewerLabel(job);
  const span = gapSpan(job);
  const size = gapBytes(job);
  const filling = !terminal.has(job.state);
  const filePct = coverage(job);
  const failure = failureReason(job);
  return (
    <article className="border-b border-base-content/10 last:border-0">
      <button
        type="button"
        aria-haspopup="dialog"
        className="block w-full cursor-pointer px-4 py-4 text-left transition-colors hover:bg-base-300/40 focus-visible:bg-base-300/40 focus-visible:outline-none"
        onClick={() => onOpen(job)}
      >
        <span className="flex flex-wrap items-center justify-between gap-2">
          <span className="min-w-0 font-semibold">{job.displayName || job.itemId}</span>
          {badge(job.state)}
        </span>
        <span className="mt-1 flex flex-wrap items-center gap-2 text-xs text-base-content/70">
          <span
            className={`tooltip tooltip-right badge badge-sm badge-soft ${reasonTone[reason.tone]}`}
            data-tip={reason.help}
          >
            {reason.label}
          </span>
          {viewer && <span>{viewer}</span>}
          <span>{when(job.updated)}</span>
        </span>
        <span className="mt-2 block text-sm">
          {gapHeadline(job)}
          {filling ? <span className="text-warning"> — filling it in now</span> : "."}
        </span>
        {failure && (
          <span
            className={`mt-1 flex flex-wrap items-center gap-2 text-xs font-semibold ${failure.tone === "error" ? "text-error" : "text-warning"}`}
          >
            {failure.title}
            {failure.remedy && (
              <span className="badge badge-sm badge-info badge-soft">{failure.remedy}</span>
            )}
          </span>
        )}
        {span && (
          <span
            className="relative mt-3 block h-2.5 overflow-hidden rounded-full bg-base-content/15"
            role="img"
            aria-label={`Gap at ${Math.round(span.at * 100)}% of the file`}
            title="Where in the file this gap fill is"
          >
            <span
              className={`absolute inset-y-0 rounded-sm ${filling ? "bg-warning" : job.state === "completed" ? "bg-success" : "bg-error"}`}
              style={{ left: `${span.at * 100}%`, width: `max(8px, ${span.width * 100}%)` }}
            />
          </span>
        )}
        <span className="mt-2 flex flex-wrap justify-between gap-2 text-xs text-base-content/70">
          <span>{size === null ? "Range" : `${mebibytes(size)} gap`}</span>
          <span>{filling ? formatLiveWarming(job) : formatWarmingSpeed(job)}</span>
          <span
            className="tooltip tooltip-left text-base-content/60"
            data-tip="A gap fill caches only its own range; the rest of the file fills in when played or selected by a prefetch policy."
          >
            File overall: {filePct ?? "—"}% cached
          </span>
        </span>
      </button>
    </article>
  );
}

function JobRow({ job, onOpen }: { job: Job; onOpen: (job: Job) => void }) {
  const summary = describeCoverage(job);
  const pct = summary.percent;
  const finished = terminal.has(job.state);
  const failure = failureReason(job);
  return (
    <article className="border-b border-base-content/10 last:border-0">
      <button
        type="button"
        aria-haspopup="dialog"
        className="block w-full cursor-pointer px-4 pt-4 text-left transition-colors hover:bg-base-300/40 focus-visible:bg-base-300/40 focus-visible:outline-none"
        onClick={() => onOpen(job)}
      >
        <span className="flex flex-wrap items-center justify-between gap-2">
          <span className="min-w-0 font-semibold">{job.displayName || job.itemId}</span>
          {badge(job.state)}
        </span>
        <span className="mt-1 flex flex-wrap items-center gap-2 text-xs text-base-content/55">
          <SourceBubbles
            sources={job.sources ?? null}
            sourceCount={job.sourceCount ?? null}
            fallback={job.source || job.trigger}
          />
          <span>{when(job.updated)}</span>
        </span>
        {failure && (
          <span
            className={`mt-2 flex flex-wrap items-center gap-2 text-xs font-semibold ${failure.tone === "error" ? "text-error" : "text-warning"}`}
          >
            {failure.title}
            {failure.remedy && (
              <span className="badge badge-sm badge-info badge-soft">{failure.remedy}</span>
            )}
          </span>
        )}
        {pct !== null && (
          <span
            className="mt-3 block h-1.5 overflow-hidden rounded-full bg-base-content/10"
            aria-label={summary.label}
          >
            <span
              className={`block h-full rounded-full ${summary.complete ? "bg-success" : "bg-primary"}`}
              style={{ width: `${pct}%` }}
            />
          </span>
        )}
        <span className="mt-2 flex flex-wrap justify-between gap-2 text-xs text-base-content/65">
          <span className={summary.complete && summary.secondary ? "text-success" : undefined}>
            {summary.primary}
          </span>
          {finished && (
            <span title="Average speed of the bytes this job fetched, over its active warming time">
              {formatWarmingSpeed(job)}
            </span>
          )}
          {job.state === "running" && (
            <span
              className={job.stalled ? "font-semibold text-warning" : undefined}
              title="Speed over the last 20 seconds and the average for this job; the estimate uses the current speed"
            >
              {formatLiveWarming(job)}
            </span>
          )}
          {summary.secondary ? (
            <span
              className="tooltip tooltip-left"
              data-tip="This job only warms its own range; the rest of the file fills in when played or selected by a prefetch policy."
            >
              {summary.secondary}
            </span>
          ) : (
            pct !== null && (
              <span
                className="tooltip tooltip-left"
                data-tip="Includes bytes that were already cached before this job ran."
              >
                {pct}% whole-file coverage
              </span>
            )
          )}
        </span>
      </button>
      <details className="px-4 pb-4 pt-2 text-xs text-base-content/70">
        <summary className="cursor-pointer font-semibold text-primary">Details</summary>
        <div className="mt-2 rounded-lg bg-base-300/65 p-3">
          <p>
            Requested range: {bytes(job.start)} to{" "}
            {job.length === 0 ? "end of file" : bytes(job.start + job.length)}
          </p>
          {job.reason && <p>Reason: {job.reason}</p>}
          {job.error && <p className="text-error">{job.error}</p>}
          <p className="break-all">Item ID: {job.itemId}</p>
        </div>
      </details>
    </article>
  );
}
export default function SmartPrefetchActivityPage() {
  const readOnly = useIsReadOnly();
  const [status, setStatus] = useState<Status | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [view, setView] = useState<"activity" | "history" | "predictions">("activity");
  const [predictionRefresh, setPredictionRefresh] = useState(0);
  const [historyKind, setHistoryKind] = useState<HistoryKind>("warming");
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState("all");
  const [page, setPage] = useState(0);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const fileModal = useLibraryFileModal({ prewarmAction: "/library-file" });
  const refreshInFlight = useRef<Promise<void> | null>(null);
  const refreshSignal = useRef<AbortSignal | undefined>(undefined);
  const refresh = useCallback((signal?: AbortSignal) => {
    if (refreshInFlight.current && !refreshSignal.current?.aborted) return refreshInFlight.current;
    refreshSignal.current = signal;
    const request = (async () => {
      const response = await fetch(
        withUrlBase("/api/prefetch?includeCoverage=false"),
        signal ? { signal } : {},
      );
      if (!response.ok)
        throw new Error(`Could not load Smart Prefetch activity (${response.status}).`);
      const body = (await response.json()) as Status;
      if (!signal?.aborted) {
        setStatus(body);
        setError(null);
      }
    })();
    refreshInFlight.current = request;
    void request
      .finally(() => {
        if (refreshInFlight.current === request) refreshInFlight.current = null;
      })
      .catch(() => {});
    return request;
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        await refresh(controller.signal);
      } catch (cause) {
        if (!controller.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Could not load activity.");
      }
      if (!controller.signal.aborted) timer = setTimeout(() => void tick(), 10_000);
    };
    void tick();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [refresh]);
  const operate = async (operation: string) => {
    setBusy(true);
    try {
      const response = await fetch(withUrlBase("/api/prefetch/operations"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operation }),
      });
      if (!response.ok)
        throw new Error(`Could not ${operation.replaceAll("-", " ")} (${response.status}).`);
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Warming operation failed.");
    } finally {
      setBusy(false);
    }
  };
  const jobs = status?.jobs ?? [];
  const active = jobs.filter((job) => !terminal.has(job.state));
  const history = jobs.filter((job) => terminal.has(job.state));
  const gapHistory = history.filter(isGapFill);
  const warmingHistory = history.filter((job) => !isGapFill(job));
  const current =
    view === "activity" ? active : historyKind === "gaps" ? gapHistory : warmingHistory;
  const coverageFilter = view === "history" && (filter === "fully-cached" || filter === "partial");
  const metadataMatches = (job: Job) =>
    (coverageFilter ||
      (view === "activity"
        ? filter === "all" || job.state === filter
        : matchesHistoryFilter(job, historyKind, filter))) &&
    `${job.displayName ?? ""} ${job.itemId} ${job.source ?? job.trigger} ${(job.sources ?? []).map((source) => source.label).join(" ")}`
      .toLowerCase()
      .includes(search.toLowerCase());
  const coverageCandidates = current.filter(metadataMatches);
  const coveragePage = Math.min(page, Math.max(0, Math.ceil(coverageCandidates.length / 25) - 1));
  const coverageJobs = coverageFilter
    ? coverageCandidates
    : coverageCandidates.slice(coveragePage * 25, (coveragePage + 1) * 25);
  const deferredCoverage = usePageCoverage<{
    updated: number;
    start: number;
    length: number;
    rangeBytes: number;
    rangeCachedBytes: number;
  }>(
    "/api/prefetch/coverage",
    "jobIds",
    view === "predictions"
      ? []
      : coverageJobs
          .filter((job) => job.isRangeJob ?? (job.start !== 0 || job.length !== 0))
          .map((job) => job.id),
    status,
    25,
    true,
  );
  const enrichedCurrent = current.map((job) => {
    const result = deferredCoverage.values[job.id];
    return result && result.start === job.start && result.length === job.length
      ? { ...job, rangeBytes: result.rangeBytes, rangeCachedBytes: result.rangeCachedBytes }
      : { ...job, coveragePending: deferredCoverage.pending };
  });
  const visible = useMemo(
    () =>
      enrichedCurrent.filter(
        (job) =>
          (view === "activity"
            ? filter === "all" || job.state === filter
            : matchesHistoryFilter(job, historyKind, filter)) &&
          `${job.displayName ?? ""} ${job.itemId} ${job.source ?? job.trigger} ${(job.sources ?? []).map((source) => source.label).join(" ")}`
            .toLowerCase()
            .includes(search.toLowerCase()),
      ),
    [enrichedCurrent, filter, search, view, historyKind],
  );
  const lastPage = Math.max(0, Math.ceil(visible.length / 25) - 1);
  const shownPage = Math.min(page, lastPage);
  const pageJobs = visible.slice(shownPage * 25, (shownPage + 1) * 25);
  const running = active.filter((job) => job.state === "running").length;
  const today = new Date().toDateString();
  const completedToday = history.filter(
    (job) => job.state === "completed" && new Date(job.updated).toDateString() === today,
  ).length;
  const budget = status?.settings?.dailyByteBudget;
  const used = status?.dailyBudgetUsed;
  const pct = budget && used != null ? Math.min(100, (used / budget) * 100) : 0;
  const unavailable = !status?.available || status?.healthy === false;
  const median = view === "history" ? medianWarmingSpeed(visible) : null;
  return (
    <div className="mx-auto max-w-7xl space-y-5 px-4 py-6 sm:px-6 lg:px-8">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <p className="text-xs font-bold uppercase tracking-widest text-primary">
            Storage &amp; activity
          </p>
          <h1 className="mt-1 text-3xl font-bold tracking-tight">Smart Prefetch</h1>
          <p className="mt-1 text-sm text-base-content/65">
            Follow warming in real time, review recent outcomes, and see what’s predicted next.
          </p>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            className="btn btn-sm btn-outline"
            onClick={() => {
              setPredictionRefresh((value) => value + 1);
              void refresh().catch((cause) => setError(String(cause)));
            }}
          >
            <Icon name="refresh" /> Refresh
          </button>
          <Link className="btn btn-sm btn-outline" to={settingsPath("streaming")}>
            <Icon name="settings" /> Prefetch settings
          </Link>
        </div>
      </div>
      {error && (
        <div className="alert alert-error text-sm" role="alert">
          {error}
        </div>
      )}
      {status?.initializationError && (
        <div className="alert alert-warning text-sm">{status.initializationError}</div>
      )}
      {status?.runtimeError && (
        <div className="alert alert-warning text-sm">{status.runtimeError}</div>
      )}
      {status?.lastError && (
        <div className="alert alert-warning text-sm">Policy refresh: {status.lastError}</div>
      )}
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Card
          label="Queue status"
          value={
            !status ? "Loading…" : unavailable ? "Unavailable" : status.paused ? "Paused" : "Active"
          }
          foot={
            status?.lastSuccess
              ? `Last policy refresh ${new Date(status.lastSuccess).toLocaleString()}`
              : "No successful policy refresh yet"
          }
          color={status && !unavailable && !status.paused ? "text-success" : "text-warning"}
        />
        <Card
          label="Warming now"
          value={`${running}${status?.settings?.maxConcurrentJobs ? ` / ${status.settings.maxConcurrentJobs} slots` : ""}`}
          foot={`${active.filter((job) => job.state === "queued").length} waiting in queue`}
        />
        <Card
          label="Completed today"
          value={String(completedToday)}
          foot="Within recent retained jobs"
        />
        <div className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
          <p className="text-xs font-semibold text-base-content/65">Daily provider budget</p>
          <p className="mt-2 text-2xl font-bold">
            {status?.available ? bytes(used) : "—"}{" "}
            <span className="text-sm font-normal text-base-content/60">
              / {budget === 0 ? "unlimited" : bytes(budget)}
            </span>
          </p>
          <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-base-content/10">
            <div className="h-full rounded-full bg-warning" style={{ width: `${pct}%` }} />
          </div>
          <p className="mt-1 text-xs text-base-content/50">Accounting resets at midnight UTC</p>
        </div>
      </div>
      <div className="tabs tabs-border border-b border-base-content/10" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={view === "activity"}
          className={`tab ${view === "activity" ? "tab-active" : ""}`}
          onClick={() => {
            setView("activity");
            setSearch("");
            setFilter("all");
            setPage(0);
          }}
        >
          Activity · {active.length}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={view === "history"}
          className={`tab ${view === "history" ? "tab-active" : ""}`}
          onClick={() => {
            setView("history");
            setSearch("");
            setFilter("all");
            setPage(0);
          }}
        >
          Warming history · {history.length}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={view === "predictions"}
          className={`tab ${view === "predictions" ? "tab-active" : ""}`}
          onClick={() => setView("predictions")}
        >
          Prediction results
        </button>
      </div>
      {view === "predictions" ? (
        <PredictionResults jobs={jobs} modal={fileModal} refreshKey={predictionRefresh} />
      ) : (
        <>
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="text-lg font-semibold">
                {view === "activity" ? "Current warming" : "Warming history"}
              </h2>
              <p className="text-xs text-base-content/55">
                {view === "activity"
                  ? "Current queue jobs refresh every 10 seconds."
                  : historyKind === "gaps"
                    ? "Small ranges playback had to stream straight from Usenet because they weren't cached. Each fill caches only that range — it does not mean the whole file is cached."
                    : "Policy, manual and finish-watched warming. Coverage is for the whole file."}
              </p>
              {view === "history" && historyKind === "warming" && (
                <p className="mt-1 max-w-3xl text-xs text-base-content/55">
                  The cache keeps what was played plus the files your prefetch policies select.
                  Partially cached files fill in when they are played
                  {status?.settings?.finishWatchedEnabled
                    ? ", and after you have watched part of them."
                    : "; turn on finishing partially watched files in Prefetch settings to complete them automatically."}
                </p>
              )}
            </div>
            <div className="flex flex-wrap gap-2">
              <input
                className="input input-sm input-bordered w-56"
                type="search"
                aria-label={`Search ${view}`}
                placeholder="Search media…"
                value={search}
                onChange={(event) => {
                  setSearch(event.target.value);
                  setPage(0);
                }}
              />
              <select
                className="select select-sm select-bordered"
                aria-label={`Filter ${view}`}
                value={filter}
                onChange={(event) => {
                  setFilter(event.target.value);
                  setPage(0);
                }}
              >
                {view === "activity"
                  ? filterStates.map((state) => (
                      <option value={state} key={state}>
                        {state === "all" ? "All states" : state}
                      </option>
                    ))
                  : Object.entries(historyKind === "gaps" ? gapFilters : warmingFilters).map(
                      ([value, label]) => (
                        <option value={value} key={value}>
                          {label}
                        </option>
                      ),
                    )}
              </select>
            </div>
          </div>
          {coverageFilter && deferredCoverage.pending && (
            <p role="status" className="text-sm text-base-content/60">
              Checking current range coverage for this filter…
            </p>
          )}
          {view === "activity" ? (
            <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_19rem]">
              <JobList
                title="Warming queue"
                jobs={pageJobs}
                total={visible.length}
                loaded={status !== null}
                page={shownPage}
                lastPage={lastPage}
                onPage={setPage}
                onOpen={(job) => openJob(fileModal, job)}
              />
              <div className="space-y-4">
                <section className="space-y-3 rounded-xl border border-base-content/10 bg-base-200/50 p-4">
                  <h3 className="font-semibold">Queue controls</h3>
                  <p className="text-xs text-base-content/60">
                    Warming yields to foreground playback and respects provider limits.
                  </p>
                  <div className="flex justify-between text-xs">
                    <span>State</span>
                    <strong>
                      {status?.paused ? "Paused" : unavailable ? "Unavailable" : "Active"}
                    </strong>
                  </div>
                  <div className="flex justify-between text-xs">
                    <span>Concurrency</span>
                    <strong>
                      {running} of {status?.settings?.maxConcurrentJobs ?? "—"} jobs
                    </strong>
                  </div>
                  <button
                    type="button"
                    className="btn btn-sm btn-outline w-full"
                    disabled={readOnly || busy || unavailable}
                    onClick={() => void operate(status?.paused ? "resume-all" : "pause-all")}
                  >
                    {status?.paused ? "Resume all warming" : "Pause all warming"}
                  </button>
                  <button
                    type="button"
                    className="btn btn-sm btn-outline w-full"
                    disabled={readOnly || busy || unavailable}
                    onClick={() => void operate("sync")}
                  >
                    Sync Plex policies now
                  </button>
                </section>
                <section className="rounded-xl border border-base-content/10 bg-base-200/50 p-4">
                  <h3 className="font-semibold">Why a job might wait</h3>
                  <p className="mt-2 text-xs text-base-content/60">
                    Active playback, provider limits, exhausted budget, and unavailable sources can
                    pause warming.
                  </p>
                </section>
              </div>
            </div>
          ) : (
            <>
              <div className="tabs tabs-box w-fit" role="tablist" aria-label="Warming history kind">
                {(
                  [
                    ["warming", "Prefetch warming", warmingHistory.length],
                    ["gaps", "Playback gap fills", gapHistory.length],
                  ] as const
                ).map(([kind, label, count]) => (
                  <button
                    key={kind}
                    type="button"
                    role="tab"
                    aria-selected={historyKind === kind}
                    className={`tab gap-2 ${historyKind === kind ? "tab-active" : ""}`}
                    onClick={() => {
                      setHistoryKind(kind);
                      setFilter("all");
                      setPage(0);
                    }}
                  >
                    {label}
                    <span className="badge badge-sm">{count}</span>
                  </button>
                ))}
              </div>
              <JobList
                title={historyKind === "gaps" ? "Playback gap fills" : "Recent outcomes"}
                summary={median === null ? null : `median ${formatSpeed(median)}`}
                jobs={pageJobs}
                total={visible.length}
                loaded={status !== null}
                page={shownPage}
                lastPage={lastPage}
                onPage={setPage}
                onOpen={(job) => openJob(fileModal, job)}
              />
            </>
          )}
        </>
      )}
      <LibraryFileModalHost
        modal={fileModal}
        canPrewarm={Boolean(status?.available) && status?.healthy !== false}
      />
      {!readOnly && (
        <details
          className="collapse collapse-arrow rounded-xl border border-base-content/10 bg-base-200/50"
          onToggle={(event) => setAdvancedOpen(event.currentTarget.open)}
        >
          <summary className="collapse-title text-sm font-semibold">
            Advanced warming controls
          </summary>
          <div className="collapse-content">{advancedOpen && <PrefetchQueue />}</div>
        </details>
      )}
    </div>
  );
}

function openJob(modal: LibraryFileModalController, job: Job) {
  modal.openByDavItemId(job.itemId, {
    displayName: job.displayName || job.itemId,
    size: job.fileSize ?? null,
    cachePercentage: coverage(job),
  });
}

function JobList({
  title,
  summary = null,
  jobs,
  total,
  loaded,
  page,
  lastPage,
  onPage,
  onOpen,
}: {
  title: string;
  summary?: string | null;
  jobs: Job[];
  total: number;
  loaded: boolean;
  page: number;
  lastPage: number;
  onPage: (page: number) => void;
  onOpen: (job: Job) => void;
}) {
  return (
    <section className="overflow-hidden rounded-xl border border-base-content/10 bg-base-200/50">
      <div className="flex flex-wrap items-baseline justify-between gap-2 border-b border-base-content/10 px-4 py-3 text-sm font-semibold">
        <span>{title}</span>
        <span className="text-xs font-normal text-base-content/50">
          {summary ? `${summary} · ` : ""}
          {total} jobs
        </span>
      </div>
      {total ? (
        jobs.map((job) =>
          isGapFill(job) ? (
            <GapRow key={job.id} job={job} onOpen={onOpen} />
          ) : (
            <JobRow key={job.id} job={job} onOpen={onOpen} />
          ),
        )
      ) : (
        <p className="px-4 py-16 text-center text-sm text-base-content/60">
          {loaded ? "No matching jobs." : "Loading warming jobs…"}
        </p>
      )}
      {total > 25 && (
        <div className="flex items-center justify-between border-t border-base-content/10 p-3 text-xs">
          <button
            type="button"
            className="btn btn-xs btn-outline"
            disabled={page === 0}
            onClick={() => onPage(page - 1)}
          >
            Previous
          </button>
          <span>
            Page {page + 1} of {lastPage + 1}
          </span>
          <button
            type="button"
            className="btn btn-xs btn-outline"
            disabled={page === lastPage}
            onClick={() => onPage(page + 1)}
          >
            Next
          </button>
        </div>
      )}
    </section>
  );
}
