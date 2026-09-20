import type { Dispatch, SetStateAction } from "react";
import { Alert, Input, ManagedSetting, SettingsCard, Toggle } from "~/components/ui";
import { PlexSources } from "./plex-sources";
import { PrefetchQueue } from "./prefetch-queue";
import {
  PREFETCH_KEY,
  numericFields,
  parsePrefetchSettings,
  validatePrefetchSettings,
  type PrefetchSettings,
} from "./smart-prefetch-model";

const toggles: {
  key: keyof Pick<
    PrefetchSettings,
    | "Enabled"
    | "HistoryEnabled"
    | "RealtimeEnabled"
    | "ReadActivityEnabled"
    | "PredictionsEnabled"
    | "MinimumWarmEnabled"
    | "FullFileWarming"
    | "MovieEnabled"
    | "TvEnabled"
    | "WarmLocalFiles"
    | "PauseDuringPlayback"
  >;
  label: string;
}[] = [
  { key: "Enabled", label: "Enable Smart Prefetch" },
  { key: "HistoryEnabled", label: "Use Plex watch history" },
  { key: "RealtimeEnabled", label: "Use verified Plex playback" },
  { key: "ReadActivityEnabled", label: "Use raw read-activity signals" },
  { key: "PredictionsEnabled", label: "Predict upcoming episodes" },
  { key: "MinimumWarmEnabled", label: "Warm minimum head and tail ranges" },
  { key: "FullFileWarming", label: "Warm full files" },
  { key: "MovieEnabled", label: "Warm movies" },
  { key: "TvEnabled", label: "Warm TV episodes" },
  { key: "WarmLocalFiles", label: "Allow mapped local library files" },
  { key: "PauseDuringPlayback", label: "Pause background warming during playback" },
];
export function SmartPrefetchSettings({
  config,
  setNewConfig,
}: {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
}) {
  let settings: PrefetchSettings | null = null;
  let error: string | null;
  try {
    settings = parsePrefetchSettings(config[PREFETCH_KEY]);
    error = validatePrefetchSettings(settings);
  } catch {
    error =
      "Saved Smart Prefetch settings are malformed. Correct the saved JSON or environment value before editing.";
  }
  const update = (next: PrefetchSettings) =>
    setNewConfig((current) => ({ ...current, [PREFETCH_KEY]: JSON.stringify(next) }));
  return (
    <div className="space-y-5">
      {error && <Alert variant="danger">{error}</Alert>}
      {settings && (
        <ManagedSetting configKey={PREFETCH_KEY}>
          <SettingsCard
            icon="auto_awesome"
            title="Smart Prefetch policies"
            description="Advanced and off by default; daily provider-payload budget defaults to 10 GB. Save Native cache settings and restart before enabling. Apply verifies a writable folder; disable Smart Prefetch before changing cache mode or folders. Queue actions below act immediately."
          >
            <div className="grid gap-3 md:grid-cols-2">
              {toggles.map((toggle) => (
                <Toggle
                  key={toggle.key}
                  label={toggle.label}
                  checked={settings[toggle.key]}
                  onChange={(event) => update({ ...settings, [toggle.key]: event.target.checked })}
                />
              ))}
            </div>
            <p className="text-xs text-base-content/60">
              Raw read activity is not verified Plex playback. Local-library eligibility still
              requires a symlink or STRM path that resolves to an imported DAV file; regular local
              files are skipped. It never starts a filesystem scan or another downloader. All
              warming stays below foreground playback admission.
            </p>
            <div className="grid gap-3 md:grid-cols-2">
              {numericFields.map((field) => (
                <label key={field.key}>
                  {field.label}
                  <Input
                    aria-label={field.label}
                    type="number"
                    min={field.min}
                    max={field.max}
                    step={field.step ?? 1}
                    value={settings[field.key]}
                    onChange={(event) =>
                      update({ ...settings, [field.key]: Number(event.target.value) })
                    }
                  />
                  <small className="block text-xs text-base-content/50">
                    {field.min.toLocaleString()}–{field.max.toLocaleString()}
                  </small>
                </label>
              ))}
            </div>
            <p className="text-xs">
              Connection and concurrency caps do not create extra provider capacity. Daily bytes
              include attempted provider work; cached hits do not consume the speculative budget.
              Saving a disabled trigger retires work owned only by that trigger.
            </p>
          </SettingsCard>
          <div className="mt-5">
            <SettingsCard
              icon="video_library"
              title="Plex libraries, users and sources"
              description="Choose movie or TV hubs and collections. Enable flags, per-source limits and excluded TV show IDs save with General Apply."
            >
              <PlexSources settings={settings} onChange={update} />
            </SettingsCard>
          </div>
        </ManagedSetting>
      )}
      <PrefetchQueue />
    </div>
  );
}
