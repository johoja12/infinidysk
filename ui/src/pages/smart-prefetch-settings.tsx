import { useEffect, useRef, useState } from "react";
import { useBlocker, Link } from "react-router";
import { useMutation } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { useSettings, useSaveSettings } from "../settings";
import {
  DiscardDialog,
  Failure,
  Field,
  Loading,
  Locked,
  SettingsPanel,
  Toggle,
} from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { PlexSourcePicker } from "./plex-source-picker";
type Settings = Schemas["PrefetchSettings"];
type Plex = Schemas["PlexPrefetchSettings"];
type Policy = Schemas["PlexPredictionPolicy"];
const equal = (a: unknown, b: unknown) =>
  JSON.stringify(a) === JSON.stringify(b);
/** Keep edits, while taking fresh server values for every untouched field. */
export function mergePrefetchEdits(
  draft: unknown,
  base: unknown,
  current: unknown,
): unknown {
  if (equal(draft, base)) return current;
  if (
    !draft ||
    !base ||
    !current ||
    Array.isArray(draft) ||
    typeof draft !== "object" ||
    typeof base !== "object" ||
    typeof current !== "object"
  )
    return draft;
  const d = draft as Record<string, unknown>,
    b = base as Record<string, unknown>;
  return Object.fromEntries(
    Object.entries(current).map(([k, v]) => [
      k,
      mergePrefetchEdits(d[k], b[k], v),
    ]),
  );
}
export function SmartPrefetchSettings() {
  const view = useSettings();
  const save = useSaveSettings();
  const [draft, setDraft] = useState<Settings | null>(null);
  const base = useRef<Settings | null>(null);
  useEffect(() => {
    if (!view.data) return;
    const next = view.data.settings.prefetch;
    const before = base.current;
    setDraft((d) =>
      d && before ? (mergePrefetchEdits(d, before, next) as Settings) : next,
    );
    base.current = next;
  }, [view.data]);
  const dirty =
    !!draft && !!view.data && !equal(draft, view.data.settings.prefetch);
  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      dirty &&
      (currentLocation.pathname !== nextLocation.pathname ||
        currentLocation.search !== nextLocation.search),
  );
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    addEventListener("beforeunload", warn);
    return () => removeEventListener("beforeunload", warn);
  }, [dirty]);
  const submit = useMutation({
    mutationFn: async () => {
      const latest = await body(api.GET("/api/v1/settings"));
      const prefetch = mergePrefetchEdits(
        draft,
        base.current,
        latest.settings.prefetch,
      ) as Settings;
      return save.mutateAsync({ ...latest.settings, prefetch });
    },
    onSuccess: () => {
      toast.success("Smart Prefetch settings saved");
    },
    onError: () => {},
  });
  if (view.isPending) return <Loading />;
  if (!view.data) return <Failure error={view.error} />;
  if (!draft) return <Loading />;
  const locked = (path: string) =>
    view.data.locked.some((k) => path === k || path.startsWith(k + "."));
  const plex = draft.plex;
  const policy = plex.policy;
  const patch = (v: Partial<Settings>) => setDraft({ ...draft, ...v });
  const changePlex = (v: Partial<Plex>) => patch({ plex: { ...plex, ...v } });
  const changePolicy = (v: Partial<Policy>) =>
    changePlex({ policy: { ...policy, ...v } });
  const toggle = (
    key: keyof Pick<
      Policy,
      "movies" | "television" | "full_file" | "minimum_warm"
    >,
    label: string,
    hint?: string,
  ) => (
    <Toggle
      key={key}
      id={`warming-${key}`}
      label={
        <>
          {label} <Locked locked={locked(`prefetch.plex.policy.${key}`)} />
        </>
      }
      hint={hint}
      checked={policy[key]}
      disabled={submit.isPending || locked(`prefetch.plex.policy.${key}`)}
      onChange={(v) => changePolicy({ [key]: v })}
    />
  );
  const number = (
    label: string,
    value: number,
    min: number,
    max: number,
    onChange: (n: number) => void,
    path: string,
    step: number | "any" = 1,
  ) => (
    <Field
      key={path}
      label={
        <>
          {label} <Locked locked={locked(path)} />
        </>
      }
    >
      <Input
        aria-label={label}
        type="number"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={submit.isPending || locked(path)}
        onChange={(e) => onChange(Number(e.target.value))}
      />
    </Field>
  );
  return (
    <>
      <form
        id="smart-prefetch-settings"
        className="space-y-6"
        onSubmit={(e) => {
          e.preventDefault();
          submit.mutate();
        }}
      >
        <SettingsPanel title="Smart Prefetch">
          <Toggle
            id="smart-prefetch-enabled"
            label={
              <>
                Enable Smart Prefetch{" "}
                <Locked locked={locked("prefetch.enabled")} />
              </>
            }
            hint="Keep selected files in the native cache before playback."
            checked={draft.enabled}
            disabled={submit.isPending || locked("prefetch.enabled")}
            onChange={(enabled) => patch({ enabled })}
          />
          <div className="grid gap-4 sm:grid-cols-2">
            {toggle("movies", "Movies")}
            {toggle("television", "TV episodes")}
          </div>
          {!view.data.settings.native_cache.enabled && (
            <p className="text-warning-text text-sm">
              Configure and enable{" "}
              <Link className="underline" to="/native-cache">
                Native Cache
              </Link>{" "}
              before starting warming.
            </p>
          )}
          <div className="grid gap-4 sm:grid-cols-2">
            {number(
              "Daily cache-fill allowance (GB/day)",
              draft.daily_fill_bytes / 1e9,
              0.005,
              100000,
              (n) => patch({ daily_fill_bytes: Math.round(n * 1e9) }),
              "prefetch.daily_fill_bytes",
              "any",
            )}
            {number(
              "Daily provider-response allowance (GB/day)",
              draft.daily_wire_bytes / 1e9,
              0.005,
              100000,
              (n) => patch({ daily_wire_bytes: Math.round(n * 1e9) }),
              "prefetch.daily_wire_bytes",
              "any",
            )}
          </div>
          <p className="text-xs text-muted-foreground">
            Cache hits use no allowance. Provider responses include overfetch
            and interrupted transfers; their allowance is separate from
            final-media fills.
          </p>
        </SettingsPanel>
        <SettingsPanel title="Plex sources">
          <p className="text-sm text-muted-foreground">
            Choose hubs, collections or whole libraries. Customize how many
            items to keep and which shows to exclude.
          </p>
          <PlexSourcePicker
            value={plex.sources}
            servers={view.data.settings.plex.servers}
            accounts={view.data.settings.plex.accounts}
            disabled={submit.isPending || locked("prefetch.plex.sources")}
            onChange={(sources) => changePlex({ sources })}
          />
        </SettingsPanel>
        <SettingsPanel title="Signals and predictions">
          {(
            [
              ["enabled", "Predict upcoming files"],
              ["history", "Use Plex watch history"],
              ["realtime", "Use verified Plex playback"],
            ] as const
          ).map(([key, label]) => (
            <Toggle
              key={key}
              id={`smart-plex-${key}`}
              label={
                <>
                  {label} <Locked locked={locked(`prefetch.plex.${key}`)} />
                </>
              }
              checked={plex[key]}
              disabled={submit.isPending || locked(`prefetch.plex.${key}`)}
              onChange={(v) => changePlex({ [key]: v })}
            />
          ))}
          <Toggle
            id="smart-local-files"
            label={
              <>
                Use mapped local library pointers{" "}
                <Locked locked={locked("prefetch.plex.local_files")} />
              </>
            }
            hint="Recognize imported symlinks and signed pointers through the configured Plex path mappings."
            checked={plex.local_files === "imported_pointers"}
            disabled={submit.isPending || locked("prefetch.plex.local_files")}
            onChange={(enabled) =>
              changePlex({
                local_files: enabled ? "imported_pointers" : "disabled",
              })
            }
          />
          {number(
            "Upcoming episodes per show",
            plex.next_episodes,
            1,
            20,
            (n) => changePlex({ next_episodes: n }),
            "prefetch.plex.next_episodes",
          )}
        </SettingsPanel>
        <SettingsPanel title="Warming behavior">
          {toggle("full_file", "Warm full files")}
          {toggle("minimum_warm", "Keep minimum head and tail ranges")}
          <Toggle
            id="smart-finish-watched"
            label={
              <>
                Finish caching partially watched files{" "}
                <Locked locked={locked("prefetch.finish_watched.enabled")} />
              </>
            }
            hint="Qualifying playback can queue the rest of a file. Short scanner reads do not count as playback."
            checked={draft.finish_watched.enabled}
            disabled={
              submit.isPending || locked("prefetch.finish_watched.enabled")
            }
            onChange={(enabled) =>
              patch({ finish_watched: { ...draft.finish_watched, enabled } })
            }
          />
          {draft.finish_watched.enabled &&
            number(
              "Minimum played share (%)",
              draft.finish_watched.percent,
              1,
              100,
              (n) =>
                patch({
                  finish_watched: { ...draft.finish_watched, percent: n },
                }),
              "prefetch.finish_watched.percent",
            )}
        </SettingsPanel>
        <details className="rounded-lg border bg-card p-4">
          <summary className="cursor-pointer text-sm font-medium">
            Advanced policy
          </summary>
          <div className="mt-4 space-y-4">
            <div className="grid gap-4 sm:grid-cols-2">
              {number(
                "Prediction confidence (%)",
                policy.confidence_percent,
                0,
                100,
                (n) => changePolicy({ confidence_percent: n }),
                "prefetch.plex.policy.confidence_percent",
              )}
              {number(
                "History lookback (days)",
                policy.lookback_days,
                1,
                365,
                (n) => changePolicy({ lookback_days: n }),
                "prefetch.plex.policy.lookback_days",
              )}
              {number(
                "Partial movie lookback (days)",
                policy.partial_movie_lookback_days,
                0,
                365,
                (n) => changePolicy({ partial_movie_lookback_days: n }),
                "prefetch.plex.policy.partial_movie_lookback_days",
              )}
              {number(
                "Distinct episodes before prediction",
                policy.min_distinct_episodes,
                1,
                100,
                (n) => changePolicy({ min_distinct_episodes: n }),
                "prefetch.plex.policy.min_distinct_episodes",
              )}
              {number(
                "Refresh interval (seconds)",
                plex.refresh_seconds,
                30,
                3600,
                (n) => changePlex({ refresh_seconds: n }),
                "prefetch.plex.refresh_seconds",
              )}
              {number(
                "Warming connection floor",
                draft.connections_per_job,
                1,
                8,
                (n) => patch({ connections_per_job: n }),
                "prefetch.connections_per_job",
              )}
              {number(
                "Minimum head (MB)",
                policy.head_bytes / 1e6,
                0,
                1000,
                (n) => changePolicy({ head_bytes: Math.round(n * 1e6) }),
                "prefetch.plex.policy.head_bytes",
                "any",
              )}
              {number(
                "Minimum tail (MB)",
                policy.tail_bytes / 1e6,
                0,
                1000,
                (n) => changePolicy({ tail_bytes: Math.round(n * 1e6) }),
                "prefetch.plex.policy.tail_bytes",
                "any",
              )}
            </div>
            <Toggle
              id="smart-restrict-viewers"
              label="Limit watching profiles"
              hint="With this enabled, only checked profiles contribute predictions. An empty selection allows none."
              checked={policy.restrict_viewers}
              disabled={
                submit.isPending ||
                locked("prefetch.plex.policy.restrict_viewers")
              }
              onChange={(restrict_viewers) =>
                changePolicy({ restrict_viewers })
              }
            />
            {policy.restrict_viewers &&
              view.data.settings.plex.servers
                .filter((s) => s.enabled)
                .map((s) => (
                  <section key={s.id} className="space-y-2">
                    <h3 className="text-sm font-medium">{s.name}</h3>
                    {[
                      { id: "1", name: "Server owner" },
                      ...view.data.settings.plex.accounts.map((a) => ({
                        id: a.numeric_id ?? a.id,
                        name: a.name,
                      })),
                    ]
                      .filter(
                        (a, i, all) =>
                          all.findIndex((b) => b.id === a.id) === i,
                      )
                      .map((a) => {
                        const id = `${s.id}:${a.id}`;
                        return (
                          <Toggle
                            key={id}
                            id={`profile-${encodeURIComponent(id)}`}
                            label={a.name}
                            checked={policy.viewers.includes(id)}
                            disabled={
                              submit.isPending ||
                              locked("prefetch.plex.policy.viewers")
                            }
                            onChange={(checked) =>
                              changePolicy({
                                viewers: checked
                                  ? [...policy.viewers, id]
                                  : policy.viewers.filter((v) => v !== id),
                              })
                            }
                          />
                        );
                      })}
                  </section>
                ))}
            <Field label="Globally excluded show IDs">
              <Input
                aria-label="Globally excluded show IDs"
                value={policy.excluded_shows.join(", ")}
                disabled={
                  submit.isPending ||
                  locked("prefetch.plex.policy.excluded_shows")
                }
                onChange={(e) =>
                  changePolicy({
                    excluded_shows: e.target.value
                      .split(/[\s,;]+/)
                      .filter(Boolean),
                  })
                }
              />
            </Field>
          </div>
        </details>
      </form>
      {(dirty || submit.error) && (
        <div className="sticky bottom-0 mt-6 flex flex-wrap items-center justify-end gap-2 border-t bg-background/95 py-3">
          <p className="mr-auto text-sm text-muted-foreground">
            You have unsaved changes.
          </p>
          <Button
            variant="outline"
            disabled={submit.isPending}
            onClick={() => {
              setDraft(view.data.settings.prefetch);
              base.current = view.data.settings.prefetch;
              submit.reset();
            }}
          >
            Discard
          </Button>
          <Button
            form="smart-prefetch-settings"
            type="submit"
            disabled={!dirty || submit.isPending}
          >
            {submit.isPending ? "Saving…" : "Save Smart Prefetch"}
          </Button>
        </div>
      )}
      {submit.error && <Failure error={submit.error} />}
      <DiscardDialog
        open={blocker.state === "blocked"}
        description="Your unsaved Smart Prefetch changes will be lost."
        onKeep={() => blocker.reset?.()}
        onDiscard={() => {
          setDraft(view.data.settings.prefetch);
          blocker.proceed?.();
        }}
      />
    </>
  );
}
