import { useEffect, useMemo, useState } from "react";
import { withUrlBase } from "~/utils/url-base";
import { useFileCache } from "~/components/library-file-modal/use-file-cache";
import type { LibraryFileModalController } from "~/components/library-file-modal/use-library-file-modal";
import { SourceBubbles } from "./source-bubbles";
import { decimalBytes } from "./job-coverage";
import {
  groupPredictions,
  cacheState,
  type Prediction,
  type PredictionGroup,
  type PredictionJob,
} from "./predictions";

const labels = {
  all: "All predictions",
  ready: "Fully cached",
  warming: "Warming",
  queued: "Not cached",
  partial: "Partially cached",
  unavailable: "Cache unavailable",
  missing: "Not in library",
  loading: "Loading cache",
  error: "Cache request failed",
};
type State = keyof typeof labels;

export function PredictionResults({
  jobs,
  modal,
  refreshKey,
}: {
  jobs: PredictionJob[];
  modal: LibraryFileModalController;
  refreshKey: number;
}) {
  const [snapshot, setSnapshot] = useState<{
    predictions: Prediction[];
    warning?: string;
    updatedAt?: string;
    hasSnapshot?: boolean;
    refreshing?: boolean;
    stale?: boolean;
    error?: string;
  } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [viewer, setViewer] = useState("all");
  const [filter, setFilter] = useState<State>("all");
  const [page, setPage] = useState(0);
  const [states, setStates] = useState<Record<string, State>>({});
  useEffect(() => {
    const abort = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    let pollingMs = 60_000;
    async function load() {
      try {
        const response = await fetch(withUrlBase("/api/prefetch/predictions"), {
          signal: abort.signal,
        });
        if (!response.ok)
          throw new Error(`Could not load prediction results (${response.status}).`);
        const body = (await response.json()) as NonNullable<typeof snapshot>;
        pollingMs = body.refreshing || body.hasSnapshot === false ? 2_000 : 60_000;
        if (!abort.signal.aborted) {
          setSnapshot(body);
          setError(body.error ?? null);
        }
      } catch (cause) {
        if (!abort.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Could not load predictions.");
      } finally {
        if (!abort.signal.aborted) timer = setTimeout(() => void load(), pollingMs);
      }
    }
    void load();
    return () => {
      abort.abort();
      clearTimeout(timer);
    };
  }, [refreshKey]);
  const groups = useMemo(() => groupPredictions(snapshot?.predictions ?? []), [snapshot]);
  const users = [...new Set(groups.flatMap((group) => group.viewers))].sort();
  // Keep rows mounted while filtering so live coverage can move them between filters.
  const matching = groups.filter(
    (group) =>
      (viewer === "all" || group.viewers.includes(viewer)) &&
      `${group.displayName} ${group.episodeTitle ?? ""} ${group.episodeCode}`
        .toLowerCase()
        .includes(search.toLowerCase()),
  );
  const filtered = matching.filter((group) => filter === "all" || states[group.key] === filter);
  const lastPage = Math.max(0, Math.ceil(filtered.length / 25) - 1);
  const currentPage = Math.min(page, lastPage);
  const shown = new Set(
    filtered.slice(currentPage * 25, (currentPage + 1) * 25).map((group) => group.key),
  );
  return (
    <section className="space-y-4" aria-label="Prediction results">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="text-lg font-semibold">Predicted shows</h2>
          <p className="text-xs text-base-content/55">
            What’s likely to be watched next, who it’s for, and how much is cached.
          </p>
        </div>
        <span className="text-xs text-base-content/50">
          {snapshot?.updatedAt
            ? `Updated ${new Date(snapshot.updatedAt).toLocaleTimeString()}`
            : error
              ? "No successful snapshot yet"
              : "Loading predictions…"}
          {snapshot?.refreshing && " · Refreshing…"}
          {snapshot?.hasSnapshot && snapshot.stale && " · Previous results"}
        </span>
      </div>
      {error && (
        <div className="alert alert-error text-sm" role="alert">
          {error}
          {snapshot?.hasSnapshot && " Showing the last successful results."}
        </div>
      )}
      {snapshot?.warning && <div className="alert alert-warning text-sm">{snapshot.warning}</div>}
      <div className="flex flex-wrap gap-2">
        <input
          type="search"
          className="input input-sm input-bordered w-full sm:w-60"
          aria-label="Search predicted shows"
          placeholder="Search shows or episodes…"
          value={search}
          onChange={(event) => {
            setSearch(event.target.value);
            setPage(0);
          }}
        />
        <select
          className="select select-sm select-bordered"
          aria-label="Filter prediction viewer"
          value={viewer}
          onChange={(event) => {
            setViewer(event.target.value);
            setPage(0);
          }}
        >
          <option value="all">All Plex users</option>
          {users.map((name) => (
            <option key={name} value={name}>
              {name}
            </option>
          ))}
        </select>
        <select
          className="select select-sm select-bordered"
          aria-label="Filter prediction cache status"
          value={filter}
          onChange={(event) => {
            setFilter(event.target.value as State);
            setPage(0);
          }}
        >
          {Object.entries(labels).map(([value, label]) => (
            <option key={value} value={value}>
              {value === "all" ? "All cache states" : label}
            </option>
          ))}
        </select>
        <span className="self-center text-xs text-base-content/55 sm:ml-auto">
          {filtered.length} predicted {filtered.length === 1 ? "episode" : "episodes"}
        </span>
      </div>
      <div className="flex flex-wrap gap-2">
        {Object.entries(labels).map(([value, label]) => (
          <button
            type="button"
            key={value}
            aria-pressed={filter === value}
            className={`btn btn-xs rounded-full ${filter === value ? "btn-primary btn-outline" : "btn-ghost border-base-content/10"}`}
            onClick={() => {
              setFilter(value as State);
              setPage(0);
            }}
          >
            {label} ·{" "}
            {value === "all"
              ? matching.length
              : matching.filter((group) => states[group.key] === value).length}
          </button>
        ))}
      </div>
      <div className="overflow-hidden rounded-xl border border-base-content/10 bg-base-200/50">
        <div className="hidden grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1fr)] gap-6 border-b border-base-content/10 px-4 py-3 text-xs font-semibold uppercase text-base-content/50 lg:grid">
          <span>Show / predicted episode</span>
          <span>Prediction &amp; viewer</span>
          <span>Current Native Cache</span>
        </div>
        {groups.map((group, index) => (
          <PredictionRow
            key={group.key}
            group={group}
            jobs={jobs}
            visible={shown.has(group.key)}
            delay={Math.floor(index / 4) * 250}
            onState={(state) =>
              setStates((previous) =>
                previous[group.key] === state ? previous : { ...previous, [group.key]: state },
              )
            }
            modal={modal}
          />
        ))}
        {!filtered.length && (
          <p className="p-8 text-center text-sm text-base-content/55">
            {!snapshot || snapshot.hasSnapshot === false
              ? error
                ? "Prediction refresh failed. Retrying shortly."
                : "Loading prediction results…"
              : !groups.length
                ? "No next-episode predictions. Enable Plex predictions and select viewing users in Prefetch settings."
                : "No predictions match these filters."}
          </p>
        )}
      </div>
      {lastPage > 0 && (
        <div className="flex items-center justify-end gap-3">
          <button
            type="button"
            className="btn btn-sm"
            disabled={currentPage === 0}
            onClick={() => setPage(currentPage - 1)}
          >
            Previous
          </button>
          <span className="text-xs">
            Page {currentPage + 1} of {lastPage + 1}
          </span>
          <button
            type="button"
            className="btn btn-sm"
            disabled={currentPage === lastPage}
            onClick={() => setPage(currentPage + 1)}
          >
            Next
          </button>
        </div>
      )}
      <p className="text-xs text-base-content/50">
        Verified cached bytes <span className="mx-2 inline-block h-2 w-2 bg-primary" /> · Grey areas
        are uncached or unloaded. Coverage is for the whole file. Up to 100 policy candidates
        refresh every minute; viewing results does not enqueue warming.
      </p>
    </section>
  );
}

function PredictionRow({
  group,
  jobs,
  visible,
  delay,
  onState,
  modal,
}: {
  group: PredictionGroup;
  jobs: PredictionJob[];
  visible: boolean;
  delay: number;
  onState: (state: State) => void;
  modal: LibraryFileModalController;
}) {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    const timer = setTimeout(() => setEnabled(true), delay);
    return () => clearTimeout(timer);
  }, [delay]);
  const cache = useFileCache(enabled && group.itemId ? group.itemId : null, 30_000);
  const state = cacheState(group, cache.data, jobs, {
    loading: !enabled || cache.loading,
    error: cache.error,
  });
  useEffect(() => onState(state), [state, onState]);
  const percent = cache.data
    ? Math.min(100, Math.floor((cache.data.cachedBytes / cache.data.length) * 100))
    : null;
  const active = jobs.find(
    (job) =>
      job.itemId === group.itemId &&
      !["completed", "failed", "cancelled", "expired"].includes(job.state),
  );
  return (
    <article hidden={!visible} className="border-b border-base-content/10 last:border-0">
      <div className="grid gap-4 px-4 py-4 lg:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1fr)] lg:gap-6">
        <div className="min-w-0">
          <h3 className="break-words text-sm font-semibold">{group.displayName}</h3>
          <p className="mt-1 text-xs text-base-content/70">
            <span className="mr-2 font-semibold text-info">{group.episodeCode}</span>
            {group.episodeTitle}
          </p>
          {group.fileSize > 0 && (
            <p className="mt-1 text-xs text-base-content/45">{decimalBytes(group.fileSize)}</p>
          )}
        </div>
        <div className="min-w-0">
          <SourceBubbles sources={group.sources} fallback="Next episode" />
          <p className="mt-2 text-xs text-base-content/55">{group.reasons.join(" · ")}</p>
          {group.watchStates.map((watch) => (
            <p
              key={`${watch.viewer}:${watch.server}:${watch.status}`}
              className={`mt-2 text-xs ${watch.status === "verified" ? "text-success" : "text-warning"}`}
            >
              {watch.viewer}
              {watch.server ? ` · ${watch.server}` : ""}:{" "}
              {watch.status === "verified"
                ? "Verified next-unwatched"
                : "Chronological candidate · watched status unverified"}
              {watch.warning && (
                <span className="block">
                  {watch.warning}{" "}
                  <a
                    className="link"
                    href={withUrlBase("/settings?tab=streaming#plex-connections")}
                  >
                    Plex connections
                  </a>
                </span>
              )}
            </p>
          ))}
        </div>
        <div>
          <div className="mb-2 flex justify-between gap-2 text-xs">
            <strong
              className={
                state === "ready"
                  ? "text-success"
                  : state === "warming"
                    ? "text-info"
                    : "text-base-content/65"
              }
            >
              {labels[state]}
            </strong>
            <strong>{percent === null ? "—" : `${percent}%`}</strong>
          </div>
          {cache.data ? (
            <>
              <div
                className="relative h-2 overflow-hidden rounded bg-base-content/15"
                role="img"
                aria-label={`${percent}% cached; ${cache.data.ranges.length} verified ranges${cache.data.complete ? "" : "; additional ranges not displayed"}`}
              >
                {cache.data.ranges.map((range) => (
                  <span
                    key={range.offset}
                    className={`absolute inset-y-0 ${state === "warming" ? "bg-info" : "bg-primary"}`}
                    style={{
                      left: `${(range.offset / cache.data!.length) * 100}%`,
                      width: `${(range.count / cache.data!.length) * 100}%`,
                    }}
                  />
                ))}
              </div>
              <p className="mt-2 flex justify-between gap-2 text-xs text-base-content/50">
                <span>
                  {decimalBytes(cache.data.cachedBytes)} / {decimalBytes(cache.data.length)}
                </span>
                <span>{active ? active.state : state === "ready" ? "Ready to stream" : ""}</span>
              </p>
            </>
          ) : (
            <p className="text-xs text-base-content/50">
              {cache.error ??
                (group.itemId
                  ? "Native Cache coverage is unavailable for this mapped file."
                  : "No mapped imported file. Review the mapping in prediction details.")}
            </p>
          )}
        </div>
      </div>
      <details className="px-4 pb-4">
        <summary className="cursor-pointer text-xs text-info">Prediction details</summary>
        <div className="mt-3 space-y-2 text-xs text-base-content/65">
          <p>
            Predicted for {group.viewers.join(", ")}. {group.reasons.join(" · ")}
          </p>
          {cache.data && !cache.data.complete && (
            <p>
              Showing the first available byte ranges. The percentage includes all verified cached
              bytes.
            </p>
          )}
          {group.itemId && (
            <button
              type="button"
              className="btn btn-xs btn-outline"
              onClick={() =>
                modal.openByDavItemId(group.itemId!, {
                  displayName: group.displayName,
                  size: group.fileSize,
                  cachePercentage: percent,
                })
              }
            >
              Open file details
            </button>
          )}
        </div>
      </details>
    </article>
  );
}
