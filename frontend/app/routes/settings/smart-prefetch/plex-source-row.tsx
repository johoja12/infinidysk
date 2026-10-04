import type { ReactNode } from "react";
import { Button, Toggle } from "~/components/ui";
import type { PlexSource } from "../plex/plex-api";
import {
  isHomeCollection,
  persistedSourceKey,
  type PlexCatalogueLibrary,
} from "./plex-source-catalogue";
import { setSourceEnabled } from "./plex-source-selection";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";

type PlexSourceRowProps = {
  library: PlexCatalogueLibrary;
  source: PlexSource;
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
  onCustomize: (source: PrefetchSource) => void;
  onError?: ((message: string | null) => void) | undefined;
  /** Search results name the library; inside a library it is implied. */
  showLibrary?: boolean;
  highlight?: string;
};

function highlighted(title: string, query: string | undefined): ReactNode {
  const needle = query?.trim().toLowerCase();
  if (!needle) return title;
  const index = title.toLowerCase().indexOf(needle);
  if (index < 0) return title;
  return (
    <>
      {title.slice(0, index)}
      <mark className="rounded-sm bg-warning/20 px-0.5 text-warning">
        {title.slice(index, index + needle.length)}
      </mark>
      {title.slice(index + needle.length)}
    </>
  );
}

/** One hub or collection with its on/off toggle, shared by library lists and search results. */
export function PlexSourceRow({
  library,
  source,
  settings,
  onChange,
  onCustomize,
  onError,
  showLibrary = false,
  highlight,
}: PlexSourceRowProps) {
  const persisted = settings.Sources.find(
    (item) =>
      persistedSourceKey(item.ServerId, item.Kind, item.Key) ===
      persistedSourceKey(source.serverId, source.kind, source.key),
  );
  const noun = source.kind === "collection" ? "collection" : "source";
  return (
    <div className="flex min-h-10 min-w-0 items-center gap-2 rounded-lg px-1 hover:bg-base-content/5">
      <Toggle
        className="min-h-10 min-w-0 flex-1 gap-3"
        aria-label={`Enable ${library.title} ${noun} ${source.title}`}
        label={
          <span className="flex min-w-0 items-center gap-2">
            <span className="truncate" title={source.title}>
              {highlighted(source.title, highlight)}
            </span>
            {isHomeCollection(source) && (
              <span className="badge badge-ghost badge-xs shrink-0">collection on Home</span>
            )}
            {showLibrary && (
              <span className="badge badge-secondary badge-soft badge-xs shrink-0">
                {library.title}
              </span>
            )}
          </span>
        }
        checked={persisted?.Enabled ?? false}
        onChange={(event) => {
          const result = setSourceEnabled(settings, library, source, event.target.checked);
          onError?.(result.error);
          if (!result.error) onChange(result.settings);
        }}
      />
      {persisted?.Enabled && (
        <Button
          type="button"
          size="small"
          variant="ghost"
          aria-label={`Customize ${source.title}`}
          onClick={() => onCustomize(persisted)}
        >
          Customize
        </Button>
      )}
    </div>
  );
}
