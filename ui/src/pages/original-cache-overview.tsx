import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router";
import { api, body, type Schemas } from "../api/client";
import { useSettings } from "../settings";
import { bytes, ago } from "../format";
import { Failure, Loading } from "../ui";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { CacheMeter } from "./cache-range-map";
import { source } from "./prefetch-activity-model";
const columns =
  "md:grid-cols-[minmax(0,2fr)_minmax(9rem,1.5fr)_4rem_4rem_7rem]";
export function useOriginalCacheOverview(enabled = true) {
  return useQuery({
    queryKey: ["legacy-cache-overview"],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/native-cache/legacy", {
          params: { query: { sort: "recent", coverage: "all" } },
          signal,
        }),
      ),
    refetchInterval: 15000,
    enabled,
  });
}
export function OriginalCacheOverview({
  status,
  onFiles,
}: {
  status?: Schemas["NativeCacheStatus"];
  onFiles: () => void;
}) {
  const inventory = useOriginalCacheOverview(),
    settings = useSettings();
  const warming = useQuery({
    queryKey: ["prefetch"],
    queryFn: () => body(api.GET("/api/v1/prefetch")),
    refetchInterval: 5000,
  });
  if (inventory.isPending) return <Loading />;
  if (!inventory.data)
    return (
      <Failure
        error={inventory.error ?? new Error("Cache catalogue unavailable")}
      />
    );
  const data = inventory.data,
    folders = data.folders;
  const total = folders.reduce((n, f) => n + f.allocated_bytes, 0),
    quota = folders.every((f) => f.quota_bytes != null)
      ? folders.reduce((n, f) => n + (f.quota_bytes ?? 0), 0)
      : null;
  const files = folders.reduce((n, f) => n + f.files, 0),
    held = folders.reduce((n, f) => n + f.held_files, 0),
    stats = status?.stats,
    hits = stats?.hits ?? 0,
    misses = stats?.misses ?? 0;
  const metric = (label: string, value: string, foot: string) => (
    <div className="min-w-0 rounded-lg border bg-card p-4">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-2 text-xl font-medium tabular-nums">{value}</p>
      <p className="mt-1 text-xs text-muted-foreground">{foot}</p>
    </div>
  );
  const active = warming.data?.rates.filter((r) => r.active) ?? [];
  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <div className="rounded-lg border bg-card p-4">
          <p className="text-xs text-muted-foreground">Allocated storage</p>
          <p className="mt-2 text-xl font-medium tabular-nums">
            {bytes(total)}{" "}
            {quota != null && (
              <span className="text-sm text-muted-foreground">
                / {bytes(quota)}
              </span>
            )}
          </p>
          {quota != null && quota > 0 && (
            <div className="mt-2">
              <CacheMeter
                percent={(total / quota) * 100}
                label="Total cache capacity"
              />
            </div>
          )}
          <p className="mt-1 text-xs text-muted-foreground">
            {folders.length} existing folders · includes retired allocation
          </p>
        </div>
        {metric(
          "Catalogue files",
          files.toLocaleString(),
          `${held.toLocaleString()} protected or held · includes empty rows`,
        )}
        {metric(
          "Cache hit rate",
          hits + misses
            ? `${((hits / (hits + misses)) * 100).toFixed(1)}%`
            : "—",
          `${hits.toLocaleString()} hits · ${misses.toLocaleString()} misses since restart`,
        )}
        {metric(
          "Added to existing storage",
          bytes(stats?.legacy_written_bytes ?? 0),
          "Compatible bytes committed since restart",
        )}
      </div>
      <p className="text-xs text-muted-foreground">
        Runtime counters cover verified block lookups since service restart.
        Folder totals come from the existing catalogue. These are not 24-hour
        delivered-bandwidth measurements.
      </p>
      <section aria-label="Original cache storage" className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <h2 className="text-lg font-medium">Original cache storage</h2>
            <p className="mt-1 text-xs text-muted-foreground">
              Capacity, placement and retention for the same folders used by v1.
            </p>
          </div>
          <Button size="sm" variant="outline" asChild>
            <Link to="/settings/native-cache">Manage folders</Link>
          </Button>
        </div>
        <div className="overflow-hidden rounded-lg border bg-card">
          <div
            className={`${columns} hidden gap-4 border-b px-4 py-2 text-xs text-muted-foreground md:grid`}
          >
            <span>Folder</span>
            <span>Allocated / quota</span>
            <span>Files</span>
            <span>Priority</span>
            <span>Mode</span>
          </div>
          {folders.map((f) => {
            const cfg =
              settings.data?.settings.native_cache.legacy?.write?.folders.find(
                (w) => w.id === f.id,
              );
            return (
              <div
                key={f.id}
                className={`${columns} grid gap-4 border-b p-4 last:border-0`}
              >
                <div className="min-w-0">
                  <h3 className="break-words text-sm font-medium">{f.id}</h3>
                  <p className="mt-1 break-all font-mono text-xs text-muted-foreground">
                    {f.path}
                  </p>
                </div>
                <div className="space-y-2 text-xs tabular-nums">
                  <p>
                    {bytes(f.allocated_bytes)} /{" "}
                    {f.quota_bytes == null
                      ? "No write quota"
                      : bytes(f.quota_bytes)}
                  </p>
                  {f.quota_bytes != null && f.quota_bytes > 0 && (
                    <CacheMeter
                      percent={(f.allocated_bytes / f.quota_bytes) * 100}
                      label={`${f.id} cache capacity`}
                    />
                  )}
                  <p className="text-muted-foreground">
                    {f.min_free_bytes == null
                      ? "Reserve not configured"
                      : `${bytes(f.min_free_bytes)} reserve`}
                  </p>
                </div>
                <p className="text-sm tabular-nums">
                  {f.files.toLocaleString()}
                  <span className="block text-xs text-muted-foreground">
                    {f.held_files.toLocaleString()} held
                  </span>
                </p>
                <p className="text-sm tabular-nums">{cfg?.priority ?? "—"}</p>
                <div className="space-y-2">
                  <Badge variant={f.writable ? "secondary" : "outline"}>
                    {f.writable ? "Writable" : "Read only"}
                  </Badge>
                  <p className="text-xs text-muted-foreground">
                    {f.eviction_enabled
                      ? "Eviction permitted"
                      : "Eviction disabled"}
                  </p>
                </div>
              </div>
            );
          })}
        </div>
        <details className="rounded-lg border p-3 text-xs text-muted-foreground">
          <summary className="cursor-pointer">
            Pending publication and retirement
          </summary>
          <div className="mt-3 space-y-2">
            {folders.map((f) => (
              <p key={f.id}>
                {f.id}: {bytes(f.pending_bytes)} pending ·{" "}
                {bytes(f.retired_bytes)} retired
              </p>
            ))}
            <p>
              {data.recency_pending} recency updates pending ·{" "}
              {data.recency_dropped} dropped. Uncertain recency, active readers
              and protected pins hold eviction.
            </p>
          </div>
        </details>
      </section>
      <section className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-lg font-medium">Active warming</h2>
          <Button variant="ghost" size="sm" asChild>
            <Link to="/smart-prefetch">Open Smart Prefetch</Link>
          </Button>
        </div>
        <div className="rounded-lg border bg-card">
          {warming.error && (
            <div className="p-4">
              <Failure error={warming.error} />
            </div>
          )}
          {active.length === 0 && !warming.error && (
            <p className="p-4 text-sm text-muted-foreground">
              {warming.isPending
                ? "Loading warming activity…"
                : "No active warming transfers."}
            </p>
          )}
          {active.map((r) => {
            const job = warming.data?.jobs.find(
              (j) => j.id === r.job && j.run === r.run,
            );
            return (
              job && (
                <div
                  key={r.job}
                  className="flex flex-wrap items-center justify-between gap-3 border-b p-4 last:border-0"
                >
                  <div className="min-w-0">
                    <Link
                      className="break-words text-sm font-medium hover:text-primary"
                      to={`/files/${job.item}`}
                    >
                      {warming.data?.names[job.item] || job.item.slice(0, 8)}
                    </Link>
                    <p className="mt-1 text-xs text-muted-foreground">
                      {source(job)}
                    </p>
                  </div>
                  <div className="text-xs tabular-nums">
                    <p>{bytes(job.verified_bytes)} verified</p>
                    <p className="text-muted-foreground">
                      {r.recent_fill_bytes_per_second == null
                        ? "Collecting speed"
                        : `${bytes(r.recent_fill_bytes_per_second)}/s native fill`}
                    </p>
                  </div>
                </div>
              )
            );
          })}
        </div>
      </section>
      <section className="space-y-3">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-lg font-medium">Recently accessed</h2>
          <Button type="button" variant="ghost" size="sm" onClick={onFiles}>
            Browse files
          </Button>
        </div>
        <div className="rounded-lg border bg-card">
          {data.entries.slice(0, 8).map((e) => (
            <div
              key={e.key}
              className="grid gap-2 border-b p-4 text-xs last:border-0 sm:grid-cols-[minmax(0,1fr)_8rem_8rem]"
            >
              <div className="min-w-0">
                <p
                  className="truncate text-sm font-medium"
                  title={e.name || e.item}
                >
                  {e.name || e.item}
                </p>
                <p className="mt-1 text-muted-foreground">{e.folder}</p>
              </div>
              <p className="tabular-nums">
                {bytes(e.cached_bytes)} / {bytes(e.size)}
              </p>
              <p className="text-muted-foreground">{ago(e.last_used_ms)}</p>
            </div>
          ))}
          {!data.entries.length && (
            <p className="p-4 text-sm text-muted-foreground">
              No cached files recorded.
            </p>
          )}
        </div>
      </section>
    </div>
  );
}
