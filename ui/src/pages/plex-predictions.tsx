import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings } from "../settings";
import { bytes } from "../format";
import { Failure, Field, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
type Options = Schemas["PlexPrefetchSettings"];
type Policy = Schemas["PlexPredictionPolicy"];
export function PlexPredictions() {
  const view = useSettings(),
    save = useSaveSettings(),
    client = useQueryClient();
  const snapshot = useQuery({
    queryKey: ["plex-predictions"],
    queryFn: () => body(api.GET("/api/v1/prefetch/predictions")),
    refetchInterval: 2000,
  });
  const refresh = useMutation({
    mutationFn: () => body(api.POST("/api/v1/prefetch/predictions")),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["plex-predictions"] });
    },
  });
  const change = useMutation({
    mutationFn: async (patch: {
      options?: Partial<Options>;
      policy?: Partial<Policy>;
    }) => {
      const latest = await body(api.GET("/api/v1/settings")),
        current = latest.settings.prefetch.plex!;
      await save.mutateAsync({
        ...latest.settings,
        prefetch: {
          ...latest.settings.prefetch,
          plex: {
            ...current,
            ...patch.options,
            policy: { ...current.policy, ...patch.policy },
          },
        },
      });
      void client.invalidateQueries({ queryKey: ["plex-predictions"] });
    },
  });
  if (!view.data) return null;
  const value = view.data.settings.prefetch.plex!,
    policy = value.policy!;
  const locked = (path: string) =>
    view.data.locked.some((key) => key === path || path.startsWith(key + "."));
  return (
    <SettingsPanel title="Plex predictions">
      <p className="text-sm text-muted-foreground">
        Uses verified viewer state and exact path mappings. Preview can refresh
        with warming disabled; downloading requires Enable warming. Incomplete
        refreshes retain the last complete result.
      </p>
      {(
        [
          ["enabled", "Enable Plex predictions"],
          ["history", "Predict from recent history"],
          ["realtime", "Predict from active playback"],
        ] as const
      ).map(([key, label]) => (
        <Toggle
          key={key}
          id={`prediction-${key}`}
          label={
            <>
              {label} <Locked locked={locked(`prefetch.plex.${key}`)} />
            </>
          }
          checked={value[key] ?? false}
          disabled={change.isPending || locked(`prefetch.plex.${key}`)}
          onChange={(checked) => change.mutate({ options: { [key]: checked } })}
        />
      ))}
      <Toggle
        id="prediction-local"
        label={
          <>
            Resolve mapped local library pointers{" "}
            <Locked locked={locked("prefetch.plex.local_files")} />
          </>
        }
        hint="Recognizes imported symlinks and signed STRM pointers. It does not warm arbitrary local media."
        checked={value.local_files === "imported_pointers"}
        disabled={change.isPending || locked("prefetch.plex.local_files")}
        onChange={(checked) =>
          change.mutate({
            options: {
              local_files: checked ? "imported_pointers" : "disabled",
            },
          })
        }
      />
      <div className="grid gap-4 sm:grid-cols-2">
        <ApplyNumber
          id="prediction-refresh"
          label="Refresh interval (seconds)"
          value={value.refresh_seconds ?? 60}
          min={30}
          max={3600}
          disabled={change.isPending || locked("prefetch.plex.refresh_seconds")}
          apply={(n) => change.mutateAsync({ options: { refresh_seconds: n } })}
        />
        <ApplyNumber
          id="prediction-next"
          label="Next episodes per show"
          value={value.next_episodes ?? 2}
          min={1}
          max={20}
          disabled={change.isPending || locked("prefetch.plex.next_episodes")}
          apply={(n) => change.mutateAsync({ options: { next_episodes: n } })}
        />
        {(
          [
            ["lookback_days", "History lookback (days)", 1, 365, 7],
            [
              "partial_movie_lookback_days",
              "Partial-movie lookback (days)",
              0,
              365,
              7,
            ],
            ["min_distinct_episodes", "Minimum distinct episodes", 1, 100, 2],
            ["confidence_percent", "Confidence threshold (%)", 0, 100, 50],
          ] as const
        ).map(([key, label, min, max, fallback]) => (
          <ApplyNumber
            key={key}
            id={`prediction-${key}`}
            label={label}
            value={policy[key] ?? fallback}
            min={min}
            max={max}
            disabled={change.isPending || locked(`prefetch.plex.policy.${key}`)}
            apply={(n) => change.mutateAsync({ policy: { [key]: n } })}
          />
        ))}
      </div>
      <p className="text-xs text-muted-foreground">
        Confidence reaches 50% at the minimum episode count and 100% at twice
        that count. Time-based ranges are hints; playback remains authoritative
        for seeking.
      </p>
      {(
        [
          ["movies", "Include movies"],
          ["television", "Include television"],
          ["full_file", "Warm complete files"],
          ["minimum_warm", "Keep minimum head and tail coverage for partial warming"],
        ] as const
      ).map(([key, label]) => (
        <Toggle
          key={key}
          id={`prediction-${key}`}
          label={
            <>
              {label} <Locked locked={locked(`prefetch.plex.policy.${key}`)} />
            </>
          }
          checked={policy[key] ?? (key !== "minimum_warm")}
          disabled={change.isPending || locked(`prefetch.plex.policy.${key}`)}
          onChange={(checked) => change.mutate({ policy: { [key]: checked } })}
        />
      ))}
      <div className="grid gap-3 sm:grid-cols-2">
        {([['head_bytes','Minimum head (MB)'],['tail_bytes','Minimum tail (MB)']] as const).map(([key,label])=><ApplyNumber key={key} id={`prediction-${key}`} label={label} value={policy[key]/1000000} min={0} max={1000} disabled={change.isPending || locked(`prefetch.plex.policy.${key}`)} apply={n=>change.mutateAsync({policy:{[key]:n*1000000}})} />)}
      </div>
      <ApplyList
        id="prediction-viewers"
        label="Viewer identities"
        hint="Comma-separated server:viewer IDs. Empty includes profiles that can be verified. Display names do not identify profiles."
        value={policy.viewers ?? []}
        disabled={change.isPending || locked("prefetch.plex.policy.viewers")}
        apply={(ids) => change.mutateAsync({ policy: { viewers: ids } })}
      />
      <ApplyList
        id="prediction-excluded"
        label="Excluded show keys"
        hint="Comma-separated Plex show keys; these exclusions apply to all configured servers."
        value={policy.excluded_shows ?? []}
        disabled={
          change.isPending || locked("prefetch.plex.policy.excluded_shows")
        }
        apply={(ids) => change.mutateAsync({ policy: { excluded_shows: ids } })}
      />
      {change.error && <Failure error={change.error} />}
      {snapshot.error && <Failure error={snapshot.error} />}
      <div className="flex flex-wrap items-center gap-3">
        <Button
          type="button"
          variant="outline"
          disabled={refresh.isPending || snapshot.data?.refreshing}
          onClick={() => refresh.mutate()}
        >
          Refresh predictions
        </Button>
        <p className="text-sm text-muted-foreground">
          {snapshot.data?.refreshing
            ? "Refreshing"
            : snapshot.data?.stale
              ? "Stale result"
              : "Current result"}
          {snapshot.data?.updated_at
            ? ` · ${new Date(snapshot.data.updated_at).toLocaleString()}`
            : ""}
        </p>
      </div>
      {refresh.error && <Failure error={refresh.error} />}
      {snapshot.data?.error && (
        <p className="text-sm text-warning-text" role="status">
          {snapshot.data.error} · Showing the last complete result when
          available.
        </p>
      )}
      {snapshot.data?.warning && (
        <p className="text-sm text-warning-text" role="status">
          {snapshot.data.warning}
        </p>
      )}
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead className="text-muted-foreground">
            <tr>
              <th className="p-2">Candidate</th>
              <th className="p-2">Viewer</th>
              <th className="p-2">Warming</th>
            </tr>
          </thead>
          <tbody>
            {snapshot.data?.predictions.map((p) => (
              <tr key={p.id} className="border-t">
                <td className="p-2">
                  <p>{p.title}</p>
                  <p className="text-xs text-muted-foreground">
                    {
                      {
                        playback: "Playing file",
                        next_episode: "Next episode",
                        partial_movie: "Partially watched",
                      }[p.source]
                    }
                  </p>
                </td>
                <td className="p-2 break-all font-mono text-xs">
                  {p.server}:{p.viewer}
                </td>
                <td className="p-2">
                  {p.warning ||
                    `${bytes(p.ranges.reduce((n, r) => n + r.end - r.start, 0))} eligible`}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {snapshot.data?.predictions.length === 0 && (
        <p className="text-sm text-muted-foreground">
          No matching candidates in the last complete refresh.
        </p>
      )}
    </SettingsPanel>
  );
}
function ApplyNumber({
  id,
  label,
  value,
  min,
  max,
  disabled,
  apply,
}: {
  id: string;
  label: string;
  value: number;
  min: number;
  max: number;
  disabled: boolean;
  apply: (n: number) => Promise<unknown>;
}) {
  const [draft, setDraft] = useState<string | null>(null),
    n = Number(draft);
  return (
    <Field id={id} label={label}>
      <div className="flex gap-2">
        <Input
          id={id}
          type="number"
          min={min}
          max={max}
          value={draft ?? String(value)}
          disabled={disabled}
          onChange={(e) => setDraft(e.target.value)}
        />
        <Button
          type="button"
          variant="outline"
          disabled={
            disabled ||
            draft === null ||
            !Number.isInteger(n) ||
            n < min ||
            n > max
          }
          onClick={() => {
            void apply(n)
              .then(() => setDraft(null))
              .catch(() => {});
          }}
        >
          Apply
        </Button>
      </div>
    </Field>
  );
}
function ApplyList({
  id,
  label,
  hint,
  value,
  disabled,
  apply,
}: {
  id: string;
  label: string;
  hint: string;
  value: string[];
  disabled: boolean;
  apply: (ids: string[]) => Promise<unknown>;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  return (
    <Field id={id} label={label} hint={hint}>
      <div className="flex gap-2">
        <Input
          id={id}
          value={draft ?? value.join(", ")}
          disabled={disabled}
          onChange={(e) => setDraft(e.target.value)}
        />
        <Button
          type="button"
          variant="outline"
          disabled={disabled || draft === null}
          onClick={() => {
            const ids = Array.from(
              new Set(
                (draft ?? "")
                  .split(",")
                  .map((s) => s.trim())
                  .filter(Boolean),
              ),
            );
            void apply(ids)
              .then(() => setDraft(null))
              .catch(() => {});
          }}
        >
          Apply
        </Button>
      </div>
    </Field>
  );
}
