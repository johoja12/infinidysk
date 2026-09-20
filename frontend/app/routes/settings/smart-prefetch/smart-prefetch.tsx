import type { Dispatch, SetStateAction } from "react";
import { Alert, ManagedSetting, SettingsCard } from "~/components/ui";
import { PlexSources } from "./plex-sources";
import { PrefetchQueue } from "./prefetch-queue";
import {
  PREFETCH_KEY,
  parsePrefetchSettings,
  validatePrefetchSettings,
  type PrefetchSettings,
} from "./smart-prefetch-model";
import { SmartPrefetchPolicyControls } from "./smart-prefetch-policy-controls";
import { PlexServerStatus } from "./plex-server-status";
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
            title="Smart Prefetch"
            description="Keep the next thing you watch ready. Save Native cache settings and restart before enabling."
          >
            <SmartPrefetchPolicyControls settings={settings} error={error} onChange={update} />
            <PlexServerStatus />
            <p className="text-xs text-base-content/60">
              Raw read activity is not verified Plex playback. Local-library eligibility still
              requires a symlink or STRM path that resolves to an imported DAV file; regular local
              files are skipped. Connection and concurrency caps do not create extra provider
              capacity, and all warming remains below foreground playback admission.
            </p>
          </SettingsCard>
          <details className="collapse collapse-arrow mt-5 border border-base-content/10 bg-base-200/40">
            <summary className="collapse-title text-sm font-semibold">
              Plex libraries and sources
            </summary>
            <div className="collapse-content">
              <SettingsCard
                icon="video_library"
                title="Plex libraries, users and sources"
                description="Choose movie or TV hubs and collections. Enable flags, per-source limits and excluded TV show IDs save with General Apply."
              >
                <PlexSources settings={settings} onChange={update} />
              </SettingsCard>
            </div>
          </details>
        </ManagedSetting>
      )}
      <details className="collapse collapse-arrow border border-base-content/10 bg-base-200/40">
        <summary className="collapse-title text-sm font-semibold">Prefetch activity</summary>
        <div className="collapse-content">
          <PrefetchQueue />
        </div>
      </details>
    </div>
  );
}
