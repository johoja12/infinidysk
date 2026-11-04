import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings } from "../settings";
import { bytes } from "../format";
import { Failure, Field, Loading, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PlexPredictions } from "./plex-predictions";

type Operation = Schemas["PrefetchOperation"];
export function PrefetchPanel() {
  const view = useSettings();
  const save = useSaveSettings();
  const client = useQueryClient();
  const [item, setItem] = useState("");
  const [budget, setBudget] = useState<string | null>(null);
  const [wireBudget, setWireBudget] = useState<string | null>(null);
  const status = useQuery({
    queryKey: ["prefetch"],
    queryFn: () => body(api.GET("/api/v1/prefetch")),
    refetchInterval: 2000,
  });
  const refresh = () => {
    void client.invalidateQueries({ queryKey: ["prefetch"] });
  };
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
    mutationFn: (request: Schemas["PrefetchRequest"]) =>
      body(api.POST("/api/v1/prefetch", { body: request })),
    onSuccess: () => {
      setItem("");
      refresh();
    },
  });
  const settings = useMutation({
    mutationFn: async (change: Partial<Schemas["PrefetchSettings"]>) => {
      const latest = await body(api.GET("/api/v1/settings"));
      await save.mutateAsync({
        ...latest.settings,
        prefetch: { ...latest.settings.prefetch, ...change },
      });
    },
  });
  if (view.isPending) return <Loading />;
  if (!view.data) return <Failure error={view.error} />;
  const locked = (path: string) =>
    view.data.locked.some((key) => key === path || path.startsWith(key + "."));
  const value = view.data.settings.prefetch;
  return (
    <div className="space-y-6">
      <SettingsPanel title="Background warming">
        <p className="text-muted-foreground text-sm">
          Keeps verified blocks in the same native cache used by playback. Jobs
          survive restarts. Plex predictions can add verified current and next files.
        </p>
        <Toggle
          id="prefetch-enabled"
          label={
            <>
              Enable warming <Locked locked={locked("prefetch.enabled")} />
            </>
          }
          checked={value.enabled ?? false}
          disabled={locked("prefetch.enabled") || settings.isPending}
          onChange={(enabled) => settings.mutate({ enabled })}
        />
        <Field
          id="prefetch-budget"
          label="Daily fill allowance (GB)"
          hint="Attempted media-block fills, including failed or interrupted attempts. Article overfetch and network overhead are excluded. Resets at midnight UTC; cached blocks use no allowance."
        >
          <Input
            id="prefetch-budget"
            type="number"
            min={0.005}
            max={100000}
            step={1}
            value={
              budget ??
              String((value.daily_fill_bytes ?? 10000000000) / 1000000000)
            }
            disabled={locked("prefetch.daily_fill_bytes") || settings.isPending}
            onChange={(e) => setBudget(e.target.value)}
          />
        </Field>
        <Button
          type="button"
          variant="outline"
          disabled={
            budget === null ||
            !Number.isFinite(Number(budget)) ||
            Number(budget) < 0.005 ||
            Number(budget) > 100000 ||
            locked("prefetch.daily_fill_bytes") ||
            settings.isPending
          }
          onClick={() =>
            settings.mutate(
              { daily_fill_bytes: Math.round(Number(budget) * 1000000000) },
              { onSuccess: () => setBudget(null) },
            )
          }
        >
          Apply allowance
        </Button>
        <Field id="prefetch-wire-budget" label="Daily provider response allowance (GB)" hint="Reserves credits before reading warming responses, including framing and abandoned drains. Measured after TLS; login and TCP/TLS buffering are excluded. Unused reservations can remain charged after interruption or restart. Cached playback uses no credits.">
          <Input id="prefetch-wire-budget" type="number" min={0.005} max={100000} step={1} value={wireBudget ?? String(value.daily_wire_bytes / 1000000000)} disabled={locked("prefetch.daily_wire_bytes") || settings.isPending} onChange={e => setWireBudget(e.target.value)} />
        </Field>
        <Button type="button" variant="outline" disabled={wireBudget===null || !Number.isFinite(Number(wireBudget)) || Number(wireBudget)<0.005 || Number(wireBudget)>100000 || locked("prefetch.daily_wire_bytes") || settings.isPending} onClick={() => settings.mutate({daily_wire_bytes: Math.round(Number(wireBudget)*1000000000)}, {onSuccess:()=>setWireBudget(null)})}>Apply provider allowance</Button>
        <Field id="prefetch-connections" label="Warming connection floor" hint="1 to 8 articles under transfer pressure. When capacity is free, the bounded window can grow to 24 with a target reserve of 25% of provider capacity. The floor can exceed that reserve on small pools. New scheduling shrinks under pressure; replies already sent may finish. Foreground seeks still interrupt warming.">
          <Input id="prefetch-connections" type="number" min={1} max={8} value={value.connections_per_job} disabled={locked("prefetch.connections_per_job") || settings.isPending} onChange={e=>{const connections_per_job=Number(e.target.value);if(Number.isInteger(connections_per_job)&&connections_per_job>=1&&connections_per_job<=8)settings.mutate({connections_per_job});}} />
        </Field>
        {settings.error && <Failure error={settings.error} />}
        {!view.data.settings.native_cache.enabled && (
          <p className="text-sm text-muted-foreground">
            Enable and configure the native cache before starting warming.
          </p>
        )}
      </SettingsPanel>
      <SettingsPanel title="Finish watched files">
        <Toggle id="finish-watched" label={<>Finish caching watched files <Locked locked={locked("prefetch.finish_watched.enabled")} /></>} checked={value.finish_watched.enabled} disabled={locked("prefetch.finish_watched.enabled") || settings.isPending} onChange={enabled=>settings.mutate({finish_watched:{...value.finish_watched,enabled}})} />
        <p className="text-sm text-muted-foreground">Queues a complete current source after foreground playback serves the selected share for at least two minutes and 67 MB. Repeated status polling and seek positions do not count as served bytes. Cached playback counts; fast scans do not qualify.</p>
        <Field id="finish-watched-percent" label="Minimum served share (%)"><Input id="finish-watched-percent" type="number" min={1} max={100} value={value.finish_watched.percent} disabled={locked("prefetch.finish_watched.percent") || settings.isPending} onChange={e=>{const percent=Number(e.target.value);if(Number.isInteger(percent)&&percent>=1&&percent<=100)settings.mutate({finish_watched:{...value.finish_watched,percent}});}} /></Field>
      </SettingsPanel>
      <PlexPredictions />
      <SettingsPanel title="Warming history">
        <p className="text-sm text-muted-foreground">Last 100 outcomes from up to 2000 saved records. Source and cached byte counts describe final-media bytes verified by that attempt; provider response usage is reported separately.</p>
        {status.data && !status.data.history_available && <p className="text-sm text-destructive">Warming history is unavailable; retained records were not removed.</p>}
        {status.data?.history_available && status.data.history.length===0 && <p className="text-sm text-muted-foreground">No warming outcomes recorded.</p>}
        {status.data?.history.map((row,index)=><div key={`${row.job.id}-${row.recorded_at}-${index}`} className="space-y-1 border-b py-2 text-xs"><p className="break-all font-mono">{row.job.item} · {row.job.revision}</p><p>{row.job.automatic_scope ? row.job.family==="read" ? "Finish watched" : "Plex policy" : "Manual"} · {row.job.state} · stored from source {bytes(row.job.source_bytes)} · cached {bytes(row.job.cached_bytes)}</p><p className="break-words">{row.job.ranges.map(r=>`${r.start}–${r.end}`).join(", ")}</p>{row.job.reason && <p>{row.job.reason}</p>}</div>)}
      </SettingsPanel>
      <SettingsPanel title="Warm a file">
        <p className="text-muted-foreground text-sm">
          Use the imported file ID from Explore or the cache catalogue. This
          requests the complete current source revision.
        </p>
        <Field id="prefetch-item" label="File ID">
          <Input
            id="prefetch-item"
            value={item}
            disabled={enqueue.isPending}
            onChange={(e) => setItem(e.target.value)}
            placeholder="Imported file UUID"
          />
        </Field>
        <Button
          type="button"
          disabled={
            !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
              item,
            ) || enqueue.isPending
          }
          onClick={() =>
            enqueue.mutate({
              item,
              ranges: [],
              priority: 50,
              intent: crypto.randomUUID(),
            })
          }
        >
          Queue warming
        </Button>
        {enqueue.error && <Failure error={enqueue.error} />}
      </SettingsPanel>
      <SettingsPanel title="Warming queue">
        {status.error && <Failure error={status.error} />}
        {status.data && !status.data.state_available && <p className="text-sm text-destructive">Queue storage is uncertain. New work waits for durable readback.</p>}
        {status.data && (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <p className="text-sm">
                {!value.enabled ? "Disabled" : status.data.recovery_mode ? "Held for cache recovery" : status.data.paused ? "Paused" : "Running"} ·{" "}
                {bytes(status.data.charged_fill_bytes)} charged today
              </p>
              <Button
                type="button"
                variant="outline"
                disabled={pause.isPending}
                onClick={() => pause.mutate(!status.data!.paused)}
              >
                {status.data.paused ? "Resume queue" : "Pause queue"}
              </Button>
            </div>
            <p className="text-xs text-muted-foreground">Provider response reservations: {status.data.reserved_wire_bytes == null ? "storage unavailable" : bytes(status.data.reserved_wire_bytes)} for {status.data.wire_day == null ? "an unknown UTC date" : status.data.wire_day === 0 ? "a day with no reservations yet" : new Date(status.data.wire_day * 86400000).toISOString().slice(0, 10) + " UTC"}. Received since process start: {bytes(status.data.received_wire_bytes)}.{status.data.wire_blocked ? " New provider reads are currently held." : ""}</p>
            <p className="text-xs text-muted-foreground">
              Completed means the requested blocks were verified during this
              job. Later cache eviction can remove them. Retry verifies current
              coverage again.
            </p>
            <p className="text-xs text-muted-foreground">Playback backfills waiting for durable admission: {status.data.pending_backfills}. Hints dropped at the memory limit: {status.data.dropped_backfills}. Actual uncached ranges qualify after a handle serves 17 MB. Short probes do not qualify. Saved jobs survive restart.</p>
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <thead className="text-muted-foreground">
                  <tr>
                    <th className="p-2">File</th>
                    <th className="p-2">Status</th>
                    <th className="p-2">Progress</th>
                    <th className="p-2">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {status.data.jobs
                    .slice()
                    .reverse()
                    .map((job) => (
                      <tr className="border-t" key={job.id}>
                        <td className="p-2 font-mono text-xs">
                          {job.item.slice(0, 8)}
                          <br />
                          {job.revision.slice(0, 8)}
                        </td>
                        <td className="p-2">
                          {job.state}
                          {job.family === "backfill" && <p className="text-xs text-muted-foreground">Playback backfill</p>}
                          {job.reason && (
                            <p className="max-w-64 text-xs text-muted-foreground">
                              {job.reason}
                            </p>
                          )}
                        </td>
                        <td className="p-2 whitespace-nowrap">
                          {bytes(job.verified_bytes)} /{" "}
                          {bytes(
                            job.ranges.reduce((n, r) => n + r.end - r.start, 0),
                          )}
                          <p className="text-muted-foreground text-xs">
                            Stored from source: {bytes(job.source_bytes)}
                          </p>
                        </td>
                        <td className="p-2">
                          <div className="flex flex-wrap gap-1">
                            {(
                              (job.state === "queued"
                                ? ["pause", "cancel"]
                                : job.state === "paused"
                                  ? ["resume", "cancel"]
                                  : job.state === "failed"
                                    ? ["retry", "cancel"]
                                    : job.state === "complete" ||
                                        job.state === "expired"
                                      ? ["retry"]
                                      : []) as Operation[]
                            ).map((operation) => (
                              <Button
                                key={operation}
                                type="button"
                                size="sm"
                                variant="ghost"
                                disabled={control.isPending}
                                onClick={() =>
                                  control.mutate({ id: job.id, operation })
                                }
                              >
                                {operation.charAt(0).toUpperCase() +
                                  operation.slice(1)}
                              </Button>
                            ))}
                          </div>
                        </td>
                      </tr>
                    ))}
                </tbody>
              </table>
            </div>
            {status.data.jobs.length === 0 && (
              <p className="text-muted-foreground text-sm">
                No warming jobs yet.
              </p>
            )}
          </>
        )}
        {pause.error && <Failure error={pause.error} />}
        {control.error && <Failure error={control.error} />}
      </SettingsPanel>
    </div>
  );
}
