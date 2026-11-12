import { useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings } from "../settings";
import { Failure, Field, Loading, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PrefetchActivity } from "./prefetch-activity";
import { PlexPredictions } from "./plex-predictions";

export function PrefetchPanel({
  section = "all",
}: {
  section?: "all" | "activity" | "predictions" | "settings";
}) {
  const view = useSettings();
  const save = useSaveSettings();
  const [budget, setBudget] = useState<string | null>(null);
  const [wireBudget, setWireBudget] = useState<string | null>(null);
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
      {(section === "all" || section === "settings") && (
        <>
          <SettingsPanel title="Smart Prefetch settings">
            <p className="text-muted-foreground text-sm">
              Keeps verified blocks in the same native cache used by playback.
              Jobs survive restarts. Plex predictions can add verified current
              and next files.
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
                disabled={
                  locked("prefetch.daily_fill_bytes") || settings.isPending
                }
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
            <Field
              id="prefetch-wire-budget"
              label="Daily provider response allowance (GB)"
              hint="Reserves credits before reading warming responses, including framing and abandoned drains. Measured after TLS; login and TCP/TLS buffering are excluded. Unused reservations can remain charged after interruption or restart. Cached playback uses no credits."
            >
              <Input
                id="prefetch-wire-budget"
                type="number"
                min={0.005}
                max={100000}
                step={1}
                value={
                  wireBudget ?? String(value.daily_wire_bytes / 1000000000)
                }
                disabled={
                  locked("prefetch.daily_wire_bytes") || settings.isPending
                }
                onChange={(e) => setWireBudget(e.target.value)}
              />
            </Field>
            <Button
              type="button"
              variant="outline"
              disabled={
                wireBudget === null ||
                !Number.isFinite(Number(wireBudget)) ||
                Number(wireBudget) < 0.005 ||
                Number(wireBudget) > 100000 ||
                locked("prefetch.daily_wire_bytes") ||
                settings.isPending
              }
              onClick={() =>
                settings.mutate(
                  {
                    daily_wire_bytes: Math.round(
                      Number(wireBudget) * 1000000000,
                    ),
                  },
                  { onSuccess: () => setWireBudget(null) },
                )
              }
            >
              Apply provider allowance
            </Button>
            <Field
              id="prefetch-connections"
              label="Warming connection floor"
              hint="1 to 8 articles under transfer pressure. When capacity is free, the bounded window can grow to 128 with a target reserve of 25% of provider capacity. The floor can exceed that reserve on small pools. New scheduling shrinks under pressure; replies already sent may finish. Foreground seeks still interrupt warming."
            >
              <Input
                id="prefetch-connections"
                type="number"
                min={1}
                max={8}
                value={value.connections_per_job}
                disabled={
                  locked("prefetch.connections_per_job") || settings.isPending
                }
                onChange={(e) => {
                  const connections_per_job = Number(e.target.value);
                  if (
                    Number.isInteger(connections_per_job) &&
                    connections_per_job >= 1 &&
                    connections_per_job <= 8
                  )
                    settings.mutate({ connections_per_job });
                }}
              />
            </Field>
            {settings.error && <Failure error={settings.error} />}
            {!view.data.settings.native_cache.enabled && (
              <p className="text-sm text-muted-foreground">
                Enable and configure the native cache before starting warming.
              </p>
            )}
          </SettingsPanel>
          <SettingsPanel title="Finish watched files">
            <Toggle
              id="finish-watched"
              label={
                <>
                  Finish caching watched files{" "}
                  <Locked locked={locked("prefetch.finish_watched.enabled")} />
                </>
              }
              checked={value.finish_watched.enabled}
              disabled={
                locked("prefetch.finish_watched.enabled") || settings.isPending
              }
              onChange={(enabled) =>
                settings.mutate({
                  finish_watched: { ...value.finish_watched, enabled },
                })
              }
            />
            <p className="text-sm text-muted-foreground">
              Queues a complete current source after foreground playback serves
              the selected share for at least two minutes and 67 MB. Repeated
              status polling and seek positions do not count as served bytes.
              Cached playback counts; fast scans do not qualify.
            </p>
            <Field id="finish-watched-percent" label="Minimum served share (%)">
              <Input
                id="finish-watched-percent"
                type="number"
                min={1}
                max={100}
                value={value.finish_watched.percent}
                disabled={
                  locked("prefetch.finish_watched.percent") ||
                  settings.isPending
                }
                onChange={(e) => {
                  const percent = Number(e.target.value);
                  if (
                    Number.isInteger(percent) &&
                    percent >= 1 &&
                    percent <= 100
                  )
                    settings.mutate({
                      finish_watched: { ...value.finish_watched, percent },
                    });
                }}
              />
            </Field>
          </SettingsPanel>
        </>
      )}
      {(section === "all" ||
        section === "predictions" ||
        section === "settings") && (
        <PlexPredictions
          showSettings={section !== "predictions"}
          showResults={section !== "settings"}
        />
      )}
      {(section === "all" || section === "activity") && <PrefetchActivity />}
    </div>
  );
}
