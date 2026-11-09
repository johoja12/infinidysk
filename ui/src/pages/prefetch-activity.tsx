import { useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronDown, Pause, Play, RefreshCw, Search } from "lucide-react";
import { api, body, type Schemas } from "../api/client";
import { useSettings } from "../settings";
import { bytes, ago } from "../format";
import { Failure, Loading } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { useFileCoverage } from "./prefetch-coverage";
import {
  requested,
  rows,
  source,
  state,
  terminal,
  viewers,
  rangeCoverage,
  type Job,
} from "./prefetch-activity-model";

type Operation = Schemas["PrefetchOperation"];
const pretty = (s: string) =>
  s.replaceAll("_", " ").replace(/^./, (c) => c.toUpperCase());
function Filter({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: [string, string][];
  onChange: (s: string) => void;
}) {
  return (
    <select
      aria-label={label}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className="h-8 min-w-0 rounded-md border bg-background px-2 text-sm"
    >
      {options.map(([v, l]) => (
        <option key={v} value={v}>
          {l}
        </option>
      ))}
    </select>
  );
}
function Meter({ value, label }: { value: number; label: string }) {
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(value)}
      className="h-1.5 overflow-hidden rounded-full bg-muted"
    >
      <div
        className="h-full rounded-full bg-primary transition-[width]"
        style={{ width: `${Math.min(100, Math.max(0, value))}%` }}
      />
    </div>
  );
}
export function PrefetchActivity() {
  const settings = useSettings(),
    client = useQueryClient();
  const [params, setParams] = useSearchParams();
  const history = params.get("view") === "history",
    gaps = params.get("kind") === "gaps";
  const search = params.get("q") ?? "",
    filter = params.get("state") ?? "all",
    origin = params.get("source") ?? "all",
    viewer = params.get("viewer") ?? "all",
    reason = params.get("reason") ?? "all";
  const sort = params.get("sort") ?? "newest",
    grouped = params.get("group") !== "off";
  const page = Math.max(0, Math.floor(Number(params.get("page")) || 0)),
    pageSize = Number(params.get("limit")) === 50 ? 50 : 20;
  const [item, setItem] = useState("");
  const paramsRef = useRef(params);
  const observedParams = useRef(params);
  if (observedParams.current !== params) {
    observedParams.current = params;
    paramsRef.current = params;
  }
  const change = (values: Record<string, string>) => {
    const next = new URLSearchParams(paramsRef.current);
    next.delete("page");
    for (const [k, v] of Object.entries(values)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    paramsRef.current = next;
    setParams(next, { replace: true });
  };
  const query = useQuery({
    queryKey: ["prefetch"],
    queryFn: () => body(api.GET("/api/v1/prefetch")),
    refetchInterval: 5000,
  });
  const refresh = () => client.invalidateQueries({ queryKey: ["prefetch"] });
  const pause = useMutation({
    mutationFn: (paused: boolean) =>
      body(api.POST("/api/v1/prefetch/pause", { body: { paused } })),
    onSuccess: refresh,
  });
  const control = useMutation({
    mutationFn: ({ id, operation }: { id: string; operation: Operation }) =>
      body(
        api.POST("/api/v1/prefetch/{id}", {
          params: { path: { id } },
          body: { operation },
        }),
      ),
    onSuccess: refresh,
  });
  const enqueue = useMutation({
    mutationFn: () =>
      body(
        api.POST("/api/v1/prefetch", {
          body: { item, ranges: [], priority: 50, intent: crypto.randomUUID() },
        }),
      ),
    onSuccess: () => {
      setItem("");
      void refresh();
    },
  });
  const data = query.data,
    now = Date.now();
  const allRows = useMemo(
    () => (data ? rows(data, history, gaps, grouped) : []),
    [data, history, gaps, grouped],
  );
  const displayState = (job: Job) =>
    state(job, data?.rates ?? [], data?.paused ?? false, now);
  const filtered = allRows.filter((r) => {
    const j = r.job;
    return (
      (filter === "all" ||
        ["fully-cached", "partial", "unknown"].includes(filter) ||
        displayState(j) === filter) &&
      (origin === "all" || r.runs.some((run) => source(run.job) === origin)) &&
      (viewer === "all" ||
        r.runs.some((run) => viewers(run.job).includes(viewer))) &&
      (reason === "all" ||
        (reason === "unrecorded" ? !j.reason : j.reason === reason)) &&
      (!search ||
        [
          data?.names[j.item],
          j.item,
          source(j),
          ...r.runs.flatMap((run) => viewers(run.job)),
        ]
          .join(" ")
          .toLowerCase()
          .includes(search.toLowerCase()))
    );
  });
  const coverageFilter = ["fully-cached", "partial", "unknown"].includes(
    filter,
  );
  const coverage = useFileCoverage(
    filtered.map((r) => r.job),
    coverageFilter,
  );
  const visible = filtered
    .filter((r) => {
      if (!coverageFilter) return true;
      const c = coverage.results.get(`${r.job.item}:${r.job.revision}`);
      if (!c) return false;
      const unknown = c.ranges == null;
      if (filter === "unknown") return unknown;
      if (unknown) return false;
      const n = rangeCoverage(
        { ...r.job, ranges: [{ start: 0, end: r.job.size }] },
        c.ranges!,
      );
      return filter === "fully-cached"
        ? r.job.size > 0 && n >= r.job.size
        : n > 0 && n < r.job.size;
    })
    .sort((a, b) =>
      sort === "oldest"
        ? a.at - b.at
        : sort === "name"
          ? (data?.names[a.job.item] ?? a.job.item).localeCompare(
              data?.names[b.job.item] ?? b.job.item,
            )
          : sort === "progress"
            ? b.job.verified_bytes / Math.max(1, requested(b.job)) -
              a.job.verified_bytes / Math.max(1, requested(a.job))
            : b.at - a.at,
    );
  const safePage = Math.min(
    page,
    Math.max(0, Math.ceil(visible.length / pageSize) - 1),
  );
  if (settings.isPending || query.isPending) return <Loading />;
  if (!data || !settings.data)
    return (
      <Failure
        error={
          query.error ??
          settings.error ??
          new Error("Warming status is unavailable")
        }
      />
    );
  const cfg = settings.data.settings.prefetch;
  const active = data.jobs.filter((j) => !terminal(j)),
    running = data.rates.filter((r) => r.active).length;
  const today = new Date().toISOString().slice(0, 10);
  const completed = rows(data, true, false, false)
    .concat(rows(data, true, true, false))
    .filter(
      (r) =>
        r.job.state === "complete" &&
        new Date(r.at).toISOString().slice(0, 10) === today,
    ).length;
  const queueState = !cfg.enabled
    ? "Disabled"
    : data.recovery_mode
      ? "Recovery hold"
      : data.paused
        ? "Paused"
        : "Active";
  const metric = (label: string, value: string, foot: string) => (
    <div className="min-w-0 rounded-lg border bg-card p-4">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-2 text-xl font-medium tabular-nums">{value}</p>
      <p className="mt-1 text-xs text-muted-foreground">{foot}</p>
    </div>
  );
  const actions = (j: Job) =>
    !data.jobs.some(
      (current) =>
        current.id === j.id &&
        current.run === j.run &&
        current.state === j.state,
    )
      ? []
      : ((j.state === "queued"
          ? ["pause", "cancel"]
          : j.state === "paused"
            ? ["resume", "cancel"]
            : j.state === "failed"
              ? ["retry", "cancel"]
              : ["complete", "expired"].includes(j.state)
                ? ["retry"]
                : []) as Operation[]);
  const allHistory = rows(data, true, false, grouped),
    gapHistory = rows(data, true, true, false);
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          Warm files before playback and see what is already cached.
        </p>
        <div className="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={query.isFetching}
            onClick={() => {
              void query.refetch();
              coverage.refresh();
            }}
          >
            <RefreshCw />
            Refresh
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={pause.isPending}
            onClick={() => pause.mutate(!data.paused)}
          >
            {data.paused ? <Play /> : <Pause />}
            {data.paused ? "Resume queue" : "Pause queue"}
          </Button>
        </div>
      </div>
      {query.error && <Failure error={query.error} />}{" "}
      {pause.error && <Failure error={pause.error} />}{" "}
      {control.error && <Failure error={control.error} />}
      {!data.state_available && (
        <p role="alert" className="text-sm text-warning-text">
          Queue storage is unavailable. New work is held until it can be saved
          safely.
        </p>
      )}
      {data.wire_blocked && (
        <p role="status" className="text-sm text-warning-text">
          Provider allowance reached or unavailable. New provider reads are
          held.
        </p>
      )}
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {metric(
          "Queue status",
          queueState,
          `${active.filter((j) => displayState(j) === "queued").length} waiting · ${active.filter((j) => displayState(j) === "deferred").length} deferred`,
        )}
        {metric(
          "Warming now",
          String(running),
          data.recent_wire_bytes_per_second == null
            ? "Collecting provider speed"
            : `${bytes(data.recent_wire_bytes_per_second)}/s provider response`,
        )}
        {metric(
          "Completed today",
          String(completed),
          "Within retained warming and gap-fill attempts",
        )}
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Daily fill budget</p>
          <p className="mt-2 text-xl font-medium tabular-nums">
            {bytes(data.charged_fill_bytes)}{" "}
            <span className="text-sm text-muted-foreground">
              / {bytes(cfg.daily_fill_bytes)}
            </span>
          </p>
          <div className="mt-2">
            <Meter
              value={
                (data.charged_fill_bytes / Math.max(1, cfg.daily_fill_bytes)) *
                100
              }
              label="Daily fill budget"
            />
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            Resets at midnight UTC
          </p>
        </div>
      </div>
      <div
        role="tablist"
        aria-label="Warming records"
        className="flex gap-1 border-b pb-2"
      >
        <Button
          role="tab"
          aria-selected={!history}
          variant={!history ? "secondary" : "ghost"}
          onClick={() =>
            change({
              view: "",
              state: "",
              reason: "",
              kind: "",
              q: "",
              source: "",
              viewer: "",
            })
          }
        >
          Activity · {active.length}
        </Button>
        <Button
          role="tab"
          aria-selected={history}
          variant={history ? "secondary" : "ghost"}
          onClick={() =>
            change({
              view: "history",
              state: "",
              reason: "",
              q: "",
              source: "",
              viewer: "",
            })
          }
        >
          Warming history · {allHistory.length + gapHistory.length}
        </Button>
      </div>
      <section
        className="space-y-3"
        aria-label={history ? "Warming history" : "Current warming"}
      >
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h2 className="text-lg font-medium">
              {history ? "Warming history" : "Current warming"}
            </h2>
            <p className="mt-1 text-xs text-muted-foreground">
              {history
                ? gaps
                  ? "Playback gap fills cache the missing range, not the whole file."
                  : "Repeated warming of the same file and range is grouped. Open Details to see each attempt."
                : "Active and waiting jobs. Completed attempts are in Warming history."}
            </p>
          </div>
          {history && (
            <Filter
              label="History type"
              value={gaps ? "gaps" : "warming"}
              options={[
                ["warming", "File warming"],
                ["gaps", "Playback gap fills"],
              ]}
              onChange={(v) => change({ kind: v, state: "", reason: "" })}
            />
          )}
        </div>
        <div className="flex flex-wrap gap-2 rounded-lg border bg-card p-3">
          <div className="relative min-w-40 flex-1">
            <Search className="absolute left-2 top-2 size-4 text-muted-foreground" />
            <Input
              type="search"
              aria-label="Search warming"
              placeholder="Search files, sources or viewers…"
              className="pl-8"
              value={search}
              onChange={(e) => change({ q: e.target.value })}
            />
          </div>
          <Filter
            label="Filter warming"
            value={filter}
            options={[
              ["all", "All states"],
              ...((history
                ? [
                    ["complete", "Completed"],
                    ["fully-cached", "Fully cached now"],
                    ["partial", "Partially cached now"],
                    ["unknown", "Coverage unknown"],
                    ["failed", "Failed"],
                    ["cancelled", "Cancelled"],
                    ["expired", "Expired"],
                    ["superseded", "Source replaced"],
                  ]
                : [
                    ["running", "Running"],
                    ["queued", "Queued"],
                    ["deferred", "Deferred"],
                    ["paused", "Paused"],
                  ]) as [string, string][]),
            ]}
            onChange={(v) => {
              change({ state: v });
            }}
          />
          <Filter
            label="Filter source"
            value={origin}
            options={[
              ["all", "All sources"],
              ...[
                ...new Set(
                  allRows.flatMap((r) => r.runs.map((run) => source(run.job))),
                ),
              ]
                .sort()
                .map((s) => [s, s] as [string, string]),
            ]}
            onChange={(v) => change({ source: v })}
          />
          <Filter
            label="Filter viewer"
            value={viewer}
            options={[
              ["all", "All viewers"],
              ...[
                ...new Set(
                  allRows.flatMap((r) =>
                    r.runs.flatMap((run) => viewers(run.job)),
                  ),
                ),
              ]
                .sort()
                .map((s) => [s, s] as [string, string]),
            ]}
            onChange={(v) => change({ viewer: v })}
          />
          {history && gaps && (
            <Filter
              label="Filter outcome reason"
              value={reason}
              options={[
                ["all", "All outcome reasons"],
                ["unrecorded", "Reason not recorded"],
                ...[
                  ...new Set(
                    allRows
                      .map((r) => r.job.reason)
                      .filter((s): s is string => !!s),
                  ),
                ]
                  .sort()
                  .map((s) => [s, s] as [string, string]),
              ]}
              onChange={(v) => change({ reason: v })}
            />
          )}
          <Filter
            label="Sort warming"
            value={sort}
            options={[
              ["newest", "Newest first"],
              ["oldest", "Oldest first"],
              ["name", "File name"],
              ["progress", "Most progress"],
            ]}
            onChange={(v) => change({ sort: v })}
          />
          <Button
            variant="ghost"
            size="sm"
            onClick={() =>
              change({
                q: "",
                state: "",
                source: "",
                viewer: "",
                reason: "",
                sort: "",
              })
            }
          >
            Clear filters
          </Button>
        </div>
        {history && !gaps && (
          <label className="flex items-center gap-2 text-xs text-muted-foreground">
            <input
              type="checkbox"
              checked={grouped}
              onChange={(e) => change({ group: e.target.checked ? "" : "off" })}
            />
            Group repeat runs
          </label>
        )}
        {history && !data.history_available && (
          <p role="alert" className="text-sm text-warning-text">
            Saved history is unavailable. Only retained queue outcomes can be
            shown.
          </p>
        )}
        {coverageFilter && (
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <p>
              {coverage.pending
                ? `Checking current cache coverage… ${coverage.results.size} of ${coverage.total}`
                : `Checked ${coverage.results.size} files. Unknown or changed revisions are never counted as cached.`}
            </p>
          </div>
        )}
        <div className="overflow-hidden rounded-lg border bg-card">
          <div className="grid grid-cols-[minmax(0,1fr)_minmax(10rem,1fr)] gap-4 border-b px-4 py-2 text-xs font-medium text-muted-foreground sm:grid-cols-[minmax(0,3fr)_7rem_minmax(8rem,1fr)_auto]">
            <span>File and source</span>
            <span className="hidden sm:block">Status</span>
            <span>Progress</span>
            <span className="hidden sm:block">Actions</span>
          </div>
          {visible
            .slice(safePage * pageSize, (safePage + 1) * pageSize)
            .map((row) => {
              const j = row.job,
                rate = data.rates.find(
                  (r) => r.job === j.id && r.run === j.run,
                ),
                total = requested(j),
                pct = Math.min(
                  100,
                  (j.verified_bytes / Math.max(1, total)) * 100,
                ),
                status = displayState(j);
              return (
                <article
                  key={row.key}
                  aria-label={`Warming file ${data.names[j.item] || j.item}`}
                  className="border-b last:border-0"
                >
                  <div className="grid min-w-0 gap-3 p-4 sm:grid-cols-[minmax(0,3fr)_7rem_minmax(8rem,1fr)_auto]">
                    <div className="min-w-0">
                      <Link
                        to={`/files/${j.item}`}
                        className="break-words text-sm font-medium hover:text-primary"
                      >
                        {data.names[j.item] || j.item.slice(0, 8)}
                      </Link>
                      <div className="mt-2 flex flex-wrap gap-1">
                        {[
                          ...new Set(row.runs.map((run) => source(run.job))),
                        ].map((s) => (
                          <Badge key={s} variant="secondary">
                            {s}
                          </Badge>
                        ))}
                        {[
                          ...new Set(
                            row.runs.flatMap((run) => viewers(run.job)),
                          ),
                        ].map((v) => (
                          <Badge key={v} variant="outline">
                            {v}
                          </Badge>
                        ))}
                        {row.runs.length > 1 && (
                          <Badge variant="outline">
                            {row.runs.length} runs
                          </Badge>
                        )}
                      </div>
                      <p className="mt-2 text-xs text-muted-foreground">
                        {ago(row.at)}
                      </p>
                    </div>
                    <div>
                      <Badge
                        variant={
                          status === "failed" ? "destructive" : "outline"
                        }
                        className={
                          status === "complete"
                            ? "text-success-text"
                            : status === "running"
                              ? "text-primary"
                              : ""
                        }
                      >
                        {pretty(status)}
                      </Badge>
                      {j.reason && (
                        <p className="mt-2 break-words text-xs text-muted-foreground">
                          {j.reason}
                        </p>
                      )}
                    </div>
                    <div className="min-w-0 space-y-2">
                      <p className="text-xs tabular-nums">
                        {bytes(j.verified_bytes)}{" "}
                        <span className="text-muted-foreground">
                          / {bytes(total)}
                        </span>
                        <span className="ml-2 text-muted-foreground">
                          {Math.round(pct)}%
                        </span>
                      </p>
                      <Meter
                        value={pct}
                        label={`Attempt progress ${data.names[j.item] || j.item}`}
                      />
                      <p className="text-xs text-muted-foreground">
                        {rate?.active
                          ? `${rate.recent_fill_bytes_per_second == null ? "Collecting speed" : bytes(rate.recent_fill_bytes_per_second) + "/s"}`
                          : `${bytes(j.source_bytes)} from source · ${bytes(j.cached_bytes)} cached`}
                      </p>
                      {rate?.stalled && (
                        <p className="text-xs text-warning-text">
                          Waiting for progress
                        </p>
                      )}
                    </div>
                    <div className="flex flex-wrap gap-1">
                      {actions(j).map((operation) => (
                        <Button
                          key={operation}
                          size="sm"
                          variant="ghost"
                          disabled={control.isPending}
                          onClick={() =>
                            control.mutate({ id: j.id, operation })
                          }
                        >
                          {pretty(operation)}
                        </Button>
                      ))}
                    </div>
                  </div>
                  <details className="px-4 pb-3 text-xs">
                    <summary className="cursor-pointer text-muted-foreground">
                      Details
                      {row.runs.length > 1
                        ? ` · ${row.runs.length} attempts`
                        : ""}
                    </summary>
                    <div className="mt-3 space-y-2 break-words text-muted-foreground">
                      <p>
                        File <span className="font-mono">{j.item}</span> ·
                        revision{" "}
                        <span className="font-mono break-all">
                          {j.revision}
                        </span>
                      </p>
                      <p>
                        Requested ranges:{" "}
                        {j.ranges
                          .map((r) => `${bytes(r.start)}–${bytes(r.end)}`)
                          .join(", ")}
                      </p>
                      <p>
                        Attempt progress records verified bytes at that time.
                        Current cache coverage can change after completion.
                      </p>
                      {j.attribution?.playback.map((p, i) => (
                        <p key={i}>
                          Plex reported {p.viewer_name || p.viewer}
                          {p.player ? ` on ${p.player}` : ""} at{" "}
                          {new Date(p.observed_at).toLocaleString()}.
                        </p>
                      ))}
                      {row.runs.length > 1 &&
                        row.runs.map((run) => (
                          <p key={`${run.job.id}:${run.job.run}`}>
                            {new Date(run.at).toLocaleString()} ·{" "}
                            {pretty(run.job.state)} ·{" "}
                            {bytes(run.job.verified_bytes)} verified ·{" "}
                            {source(run.job)}
                          </p>
                        ))}
                    </div>
                  </details>
                </article>
              );
            })}
          {!visible.length && (
            <div className="p-8 text-center">
              <p className="text-sm font-medium">
                {history ? "No matching history" : "No matching active jobs"}
              </p>
              <p className="mt-1 text-xs text-muted-foreground">
                {history
                  ? "Try another filter or history type."
                  : "Completed jobs are in Warming history."}
              </p>
            </div>
          )}
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-muted-foreground">
          <p>
            {visible.length
              ? `${safePage * pageSize + 1}–${Math.min((safePage + 1) * pageSize, visible.length)} of ${visible.length}`
              : "0 results"}{" "}
            · {history ? "retained outcomes" : "active jobs"}
          </p>
          <div className="flex items-center gap-2">
            <Filter
              label="Rows per page"
              value={String(pageSize)}
              options={[
                ["20", "20 per page"],
                ["50", "50 per page"],
              ]}
              onChange={(v) => change({ limit: v })}
            />
            <Button
              size="sm"
              variant="outline"
              disabled={!safePage}
              onClick={() => change({ page: String(safePage - 1) })}
            >
              Previous
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={(safePage + 1) * pageSize >= visible.length}
              onClick={() => change({ page: String(safePage + 1) })}
            >
              Next
            </Button>
          </div>
        </div>
      </section>
      <details className="rounded-lg border bg-card p-4">
        <summary className="flex cursor-pointer items-center gap-2 text-sm font-medium">
          <ChevronDown className="size-4" />
          Queue diagnostics and repair follow-up
        </summary>
        <div className="mt-3 space-y-2 text-xs text-muted-foreground">
          <p>
            Provider response allowance:{" "}
            {data.reserved_wire_bytes == null
              ? "Unknown"
              : bytes(data.reserved_wire_bytes)}{" "}
            reserved / {bytes(cfg.daily_wire_bytes)}. Received since restart:{" "}
            {bytes(data.received_wire_bytes)}.
          </p>
          <p>
            Playback gap fills waiting to be saved: {data.pending_backfills}.
            Dropped hints: {data.dropped_backfills}.
          </p>
          <p>
            History shows up to 100 saved outcomes plus retained queue entries.
            Outcome reasons describe warming attempts; v2 does not retain v1's
            detailed playback cache-miss cause codes.
          </p>
          {data.followup.error && <p>{data.followup.error}</p>}
          {data.followup.stale && (
            <p>Repair follow-up needs a fresh inspection.</p>
          )}
          {data.followup.entries.map((r) => (
            <p key={r.item}>
              <Link className="text-primary" to={`/files/${r.item}`}>
                {data.names[r.item] || r.item.slice(0, 8)}
              </Link>{" "}
              · {pretty(r.status)} · {pretty(r.regrab.stage)}
              {r.warmed_at && (
                <> · Full warm {new Date(r.warmed_at).toLocaleString()}</>
              )}
              {r.replacement_item && (
                <>
                  {" "}
                  · replacement{" "}
                  <Link
                    to={`/files/${r.replacement_item}`}
                    className="text-primary"
                  >
                    {data.names[r.replacement_item] ||
                      r.replacement_item.slice(0, 8)}
                  </Link>
                </>
              )}
            </p>
          ))}
        </div>
      </details>
      <details className="rounded-lg border bg-card p-4">
        <summary className="cursor-pointer text-sm font-medium">
          Warm a file manually
        </summary>
        <div className="mt-3 flex flex-wrap gap-2">
          <Input
            aria-label="File ID to warm"
            className="min-w-0 flex-1"
            placeholder="Imported file ID"
            value={item}
            onChange={(e) => setItem(e.target.value)}
          />
          <Button
            disabled={
              !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
                item,
              ) || enqueue.isPending
            }
            onClick={() => enqueue.mutate()}
          >
            Queue warming
          </Button>
        </div>
        {enqueue.error && <Failure error={enqueue.error} />}
      </details>
    </div>
  );
}
