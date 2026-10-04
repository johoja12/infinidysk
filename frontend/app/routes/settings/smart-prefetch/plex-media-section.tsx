import { useState } from "react";
import { Button, Toggle } from "~/components/ui";
import type { PlexSource } from "../plex/plex-api";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";
import {
  persistedSourceKey,
  type PlexCatalogueLibrary,
  type PlexMediaSection as PlexMediaSectionType,
} from "./plex-source-catalogue";
import { PlexSourceRow } from "./plex-source-row";
import { setLibraryEnabled, setMediaSectionEnabled } from "./plex-source-selection";

/** Rows shown per hub or collection list before "Show all". */
export const SOURCE_LIST_LIMIT = 10;

type PlexMediaSectionProps = {
  type: PlexMediaSectionType;
  title: string;
  enabled: boolean;
  libraries: PlexCatalogueLibrary[];
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
  onCustomize: (source: PrefetchSource) => void;
  onError?: (message: string | null) => void;
};

function controlId(type: string, libraryId: string, suffix: string): string {
  return `plex-${type}-${libraryId || "global"}-${suffix}`.replaceAll(/[^a-zA-Z0-9_-]/g, "-");
}

/** Enabled saved sources among a library's hubs and collections. */
export function enabledSourceCount(library: PlexCatalogueLibrary, settings: PrefetchSettings) {
  const keys = new Set(
    [...library.hubs, ...library.collections].map((source) =>
      persistedSourceKey(source.serverId, source.kind, source.key),
    ),
  );
  return settings.Sources.filter(
    (source) =>
      source.Enabled && keys.has(persistedSourceKey(source.ServerId, source.Kind, source.Key)),
  ).length;
}

export function PlexMediaSection({
  type,
  title,
  enabled,
  libraries,
  settings,
  onChange,
  onCustomize,
  onError,
}: PlexMediaSectionProps) {
  const [openLibraries, setOpenLibraries] = useState<Set<string>>(new Set());
  const [expandedLists, setExpandedLists] = useState<Set<string>>(new Set());
  const [manualChoice, setManualChoice] = useState<string | null>(null);
  const enabledSources = libraries
    .filter((library) => library.enabled)
    .reduce((total, library) => total + enabledSourceCount(library, settings), 0);
  const toggle = (current: Set<string>, update: (value: Set<string>) => void, key: string) => {
    const next = new Set(current);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    update(next);
  };

  const sourceList = (library: PlexCatalogueLibrary, label: string, sources: PlexSource[]) => {
    const listKey = `${library.identity.libraryId}:${library.identity.type}:${label}`;
    const showAll = expandedLists.has(listKey) || sources.length <= SOURCE_LIST_LIMIT + 2;
    const shown = showAll ? sources : sources.slice(0, SOURCE_LIST_LIMIT);
    return (
      <div className="flex min-w-0 flex-col gap-1">
        <h4 className="text-xs font-semibold tracking-wide text-base-content/60 uppercase">
          {label} ({sources.length})
        </h4>
        {sources.length === 0 ? (
          <p className="text-sm text-base-content/50">
            Plex returned no {label.toLowerCase()} for this library.
          </p>
        ) : (
          <>
            {shown.map((source) => (
              <PlexSourceRow
                key={`${source.kind}:${source.key}`}
                library={library}
                source={source}
                settings={settings}
                onChange={onChange}
                onCustomize={onCustomize}
                onError={onError}
              />
            ))}
            {!showAll && (
              <Button
                type="button"
                size="small"
                variant="ghost"
                className="self-start text-primary"
                onClick={() => toggle(expandedLists, setExpandedLists, listKey)}
              >
                Show all {sources.length} {label.toLowerCase()}
              </Button>
            )}
          </>
        )}
      </div>
    );
  };

  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="text-lg font-semibold">{title}</h3>
          <p className="text-xs text-base-content/60">
            {enabledSources} source{enabledSources === 1 ? "" : "s"} enabled
          </p>
        </div>
        <Toggle
          className="min-h-11"
          label={`Enable ${title} prefetch`}
          checked={enabled}
          onChange={(event) =>
            onChange(setMediaSectionEnabled(settings, type, event.target.checked))
          }
        />
      </div>
      <div className="space-y-2">
        {libraries.map((library) => {
          const key = `${library.identity.libraryId}:${library.identity.type}`;
          const isOpen = openLibraries.has(key);
          const panelId = controlId(type, library.identity.libraryId, "sources");
          const on = enabledSourceCount(library, settings);
          return (
            <div key={key} className="rounded-xl border border-base-content/15 bg-base-100/70">
              <div className="flex min-h-14 flex-wrap items-center gap-2 px-3 py-2">
                <Button
                  className="min-h-11 min-w-0 flex-1 justify-start"
                  variant="ghost"
                  aria-label={`Show ${library.title} sources`}
                  aria-expanded={isOpen}
                  aria-controls={panelId}
                  onClick={() => toggle(openLibraries, setOpenLibraries, key)}
                >
                  <span aria-hidden="true">{isOpen ? "▾" : "▸"}</span>
                  <span className="truncate">{library.title}</span>
                </Button>
                {on > 0 && <span className="badge badge-success badge-soft badge-sm">{on} on</span>}
                <span className="text-xs whitespace-nowrap text-base-content/55 tabular-nums max-sm:hidden">
                  {library.hubs.length} hubs · {library.collections.length} collections
                </span>
                <Toggle
                  className="min-h-11"
                  aria-label={`Enable ${title} library ${library.title}`}
                  label=""
                  checked={library.enabled}
                  onChange={(event) => {
                    const result = setLibraryEnabled(settings, library, event.target.checked);
                    onError?.(result.error);
                    if (result.error) return;
                    onChange(result.settings);
                    if (result.needsManualChoice) {
                      setManualChoice(key);
                      setOpenLibraries(new Set(openLibraries).add(key));
                    } else setManualChoice(null);
                  }}
                />
              </div>
              {isOpen && (
                <div id={panelId} className="space-y-3 border-t border-base-content/10 p-3">
                  {manualChoice === key && (
                    <p className="text-sm text-warning">
                      No recommended source is available. Choose a source below.
                    </p>
                  )}
                  {library.hubs.length === 0 &&
                  library.collections.length === 0 &&
                  library.unavailable.length === 0 ? (
                    <p className="text-sm text-base-content/60">
                      No compatible hubs or collections returned by Plex.
                    </p>
                  ) : (
                    <div className="grid gap-x-6 gap-y-4 lg:grid-cols-2">
                      {sourceList(library, "Hubs", library.hubs)}
                      {library.collections.length > 0 || library.identity.libraryId
                        ? sourceList(library, "Collections", library.collections)
                        : null}
                    </div>
                  )}
                  {library.unavailable.length > 0 && (
                    <div className="space-y-1 rounded-lg border border-warning/30 p-3">
                      <p className="text-sm font-medium">Saved but not currently available</p>
                      {library.unavailable.map((source) => (
                        <Toggle
                          className="min-h-11"
                          key={`${source.Kind}:${source.Key}`}
                          label={`Enable ${library.title} unavailable source ${source.Title}`}
                          checked={source.Enabled}
                          onChange={(event) =>
                            onChange({
                              ...settings,
                              Sources: settings.Sources.map((item) =>
                                item === source ? { ...item, Enabled: event.target.checked } : item,
                              ),
                            })
                          }
                        />
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </section>
  );
}
