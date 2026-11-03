import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings } from "../settings";
import { bytes } from "../format";
import { Failure, Field, Loading, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

type Operation = Schemas["PrefetchOperation"];
export function PrefetchPanel() {
  const view = useSettings();
  const save = useSaveSettings();
  const client = useQueryClient();
  const [item, setItem] = useState("");
  const [budget, setBudget] = useState<string | null>(null);
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
          survive restarts. Automatic Plex predictions are still being ported.
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
        {settings.error && <Failure error={settings.error} />}
        {!view.data.settings.native_cache.enabled && (
          <p className="text-sm text-muted-foreground">
            Enable and configure the native cache before starting warming.
          </p>
        )}
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
        {status.data && (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <p className="text-sm">
                {!value.enabled ? "Disabled" : status.data.paused ? "Paused" : "Running"} ·{" "}
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
            <p className="text-xs text-muted-foreground">
              Completed means the requested blocks were verified during this
              job. Later cache eviction can remove them. Retry verifies current
              coverage again.
            </p>
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
