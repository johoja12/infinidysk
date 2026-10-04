import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert, Button, Input, Tabs } from "~/components/ui";
import {
  loadPlexBootstrap,
  plexRequest,
  subscribePlexServers,
  type PlexLibrary,
  type PlexMedia,
  type PlexServer,
  type PlexSnapshot,
  type PlexSource,
  type PlexUser,
} from "../plex/plex-api";
import {
  buildPlexSourceCatalogue,
  enabledPlexSources,
  persistedSourceKey,
  searchPlexCatalogue,
  type PlexMediaSection as PlexMediaSectionType,
} from "./plex-source-catalogue";
import { enabledSourceCount, PlexMediaSection } from "./plex-media-section";
import { PlexSourceRow } from "./plex-source-row";
import { setPersistedSourceEnabled } from "./plex-source-selection";
import { PlexSourceCustomization } from "./plex-source-customization";
import { PlexSourceToolbar } from "./plex-source-toolbar";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";

type SourceRequest = { libraryId: string | null };

function sourceRequestMatches(source: PlexSource, request: SourceRequest): boolean {
  return (source.libraryId ?? null) === request.libraryId;
}

function latestSuccess(snapshots: Array<PlexSnapshot<unknown> | null>): string | null {
  return (
    snapshots
      .map((snapshot) => snapshot?.lastSuccess ?? null)
      .filter((value): value is string => value !== null)
      .sort()
      .at(-1) ?? null
  );
}

export function PlexSources({
  settings,
  onChange,
}: {
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
}) {
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [serverId, setServerId] = useState("");
  const [libraries, setLibraries] = useState<PlexSnapshot<PlexLibrary> | null>(null);
  const [users, setUsers] = useState<PlexSnapshot<PlexUser> | null>(null);
  const [sources, setSources] = useState<PlexSnapshot<PlexSource> | null>(null);
  const [customizing, setCustomizing] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ key: string; items: PlexMedia[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const enabledServers = useMemo(() => servers.filter((server) => server.enabled), [servers]);

  useEffect(() => {
    let alive = true;
    void loadPlexBootstrap()
      .then((result) => {
        if (alive) setServers(result.servers);
      })
      .catch((cause) => {
        if (alive) setError(cause instanceof Error ? cause.message : "Plex servers unavailable.");
      });
    return () => {
      alive = false;
    };
  }, []);

  useEffect(() => subscribePlexServers(setServers), []);

  useEffect(() => {
    if (!serverId || enabledServers.some((server) => server.id === serverId)) return;
    generation.current++;
    setServerId("");
    setLibraries(null);
    setUsers(null);
    setSources(null);
    setPreview(null);
    setCustomizing(null);
  }, [serverId, enabledServers]);

  const refresh = useCallback(
    async (forceRefresh = false) => {
      if (!serverId) return;
      const version = ++generation.current;
      setBusy(true);
      setError(null);
      try {
        const body = { serverId, forceRefresh };
        const [nextLibraries, nextUsers] = await Promise.all([
          plexRequest<PlexSnapshot<PlexLibrary>>("libraries", body),
          plexRequest<PlexSnapshot<PlexUser>>("users", body),
        ]);
        const requests: SourceRequest[] = [
          { libraryId: null },
          ...nextLibraries.data
            .filter((library) => library.type === "movie" || library.type === "show")
            .map((library) => ({ libraryId: library.id })),
        ];
        const results = await Promise.allSettled(
          requests.map((request) =>
            plexRequest<PlexSnapshot<PlexSource>>("sources", {
              serverId,
              libraryId: request.libraryId,
              forceRefresh,
            }),
          ),
        );
        if (version !== generation.current) return;
        const merged: PlexSource[] = [];
        const failures: string[] = [];
        let isStale = false;
        let lastSuccess: string | null = null;
        results.forEach((result, index) => {
          const request = requests[index]!;
          if (result.status === "fulfilled") {
            merged.push(...result.value.data);
            isStale ||= result.value.isStale;
            if (result.value.error) failures.push(result.value.error);
            if (!lastSuccess || (result.value.lastSuccess ?? "") > lastSuccess)
              lastSuccess = result.value.lastSuccess;
          } else {
            isStale = true;
            failures.push(
              result.reason instanceof Error ? result.reason.message : "Source unavailable.",
            );
            if (sources)
              merged.push(
                ...sources.data.filter((source) => sourceRequestMatches(source, request)),
              );
          }
        });
        setLibraries(nextLibraries);
        setUsers(nextUsers);
        setSources({
          data: merged,
          lastSuccess: lastSuccess ?? sources?.lastSuccess ?? null,
          isStale,
          error: failures.length > 0 ? [...new Set(failures)].join(" · ") : null,
        });
      } catch (cause) {
        if (version === generation.current) {
          setError(cause instanceof Error ? cause.message : "Plex catalogue unavailable.");
          setLibraries((current) => (current ? { ...current, isStale: true } : current));
          setUsers((current) => (current ? { ...current, isStale: true } : current));
          setSources((current) => (current ? { ...current, isStale: true } : current));
        }
      } finally {
        if (version === generation.current) setBusy(false);
      }
    },
    [serverId, sources],
  );

  useEffect(() => {
    if (!serverId) return;
    const requestGeneration = generation;
    void refresh();
    return () => {
      requestGeneration.current++;
    };
    // `refresh` also reads retained source snapshots; server changes alone start automatic loads.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverId]);

  const catalogue = useMemo(
    () =>
      buildPlexSourceCatalogue({
        serverId,
        libraries: libraries?.data ?? [],
        sources: sources?.data ?? [],
        savedSources: settings.Sources,
        disabledLibraries: settings.DisabledLibraries,
      }),
    [serverId, libraries, sources, settings.Sources, settings.DisabledLibraries],
  );
  const [query, setQuery] = useState("");
  const [chosenSection, setChosenSection] = useState<PlexMediaSectionType | null>(null);
  const section: PlexMediaSectionType =
    chosenSection ?? (catalogue.movie.length > 0 || catalogue.show.length === 0 ? "movie" : "show");
  const matches = useMemo(() => searchPlexCatalogue(catalogue, query), [catalogue, query]);
  const matchCount = matches.reduce((total, match) => total + match.sources.length, 0);
  const enabledSources = useMemo(
    () => enabledPlexSources(serverId, settings.Sources, catalogue),
    [serverId, settings.Sources, catalogue],
  );
  const sectionOn = (type: PlexMediaSectionType) =>
    catalogue[type].reduce((total, library) => total + enabledSourceCount(library, settings), 0);
  const customizeSource = (source: PrefetchSource) =>
    setCustomizing(persistedSourceKey(source.ServerId, source.Kind, source.Key));
  const customizedSource = customizing
    ? settings.Sources.find(
        (source) => persistedSourceKey(source.ServerId, source.Kind, source.Key) === customizing,
      )
    : undefined;

  const updateSource = (next: PrefetchSource) =>
    onChange({
      ...settings,
      Sources: settings.Sources.map((source) =>
        persistedSourceKey(source.ServerId, source.Kind, source.Key) ===
        persistedSourceKey(next.ServerId, next.Kind, next.Key)
          ? next
          : source,
      ),
    });

  const showPreview = async (source: PrefetchSource) => {
    const key = persistedSourceKey(source.ServerId, source.Kind, source.Key);
    setBusy(true);
    setError(null);
    try {
      const result = await plexRequest<{ items: PlexMedia[] }>("preview", {
        serverId: source.ServerId,
        key: source.Key,
        limit: Math.min(source.Limit, 20),
      });
      setPreview({ key, items: result.items });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Preview unavailable.");
    } finally {
      setBusy(false);
    }
  };

  const snapshots: Array<PlexSnapshot<unknown> | null> = [libraries, users, sources];
  const stale = snapshots.some((snapshot) => snapshot?.isStale);
  const snapshotError = snapshots.map((snapshot) => snapshot?.error).find(Boolean) ?? null;
  return (
    <div className="space-y-4">
      {error && <Alert variant="danger">{error}</Alert>}
      <PlexSourceToolbar
        servers={enabledServers}
        serverId={serverId}
        users={users?.data ?? []}
        selectedUsers={settings.Users}
        busy={busy}
        lastSuccess={latestSuccess(snapshots)}
        stale={stale}
        error={snapshotError}
        onServerChange={(next) => {
          generation.current++;
          setServerId(next);
          setLibraries(null);
          setUsers(null);
          setSources(null);
          setPreview(null);
          setCustomizing(null);
        }}
        onUsersChange={(next) => onChange({ ...settings, Users: next })}
        onRefresh={() => void refresh(true)}
      />
      {!serverId && (
        <Alert variant="info">
          Connect a Plex server first, then choose it above to configure prefetch sources.
        </Alert>
      )}
      {serverId && libraries && catalogue.movie.length === 0 && catalogue.show.length === 0 && (
        <Alert variant="info">Only movie and TV libraries can be used for Smart Prefetch.</Alert>
      )}
      {serverId && (catalogue.movie.length > 0 || catalogue.show.length > 0) && (
        <div className="space-y-4">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div className="min-w-0">
              <h3 className="text-lg font-semibold">Hubs &amp; collections</h3>
              <p className="text-sm text-base-content/60">
                Smart Prefetch warms the next items from each source you turn on.
              </p>
            </div>
            <Input
              type="search"
              className="w-full max-w-sm"
              placeholder="Filter all hubs and collections"
              aria-label="Filter hubs and collections"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
            />
          </div>
          <div
            className="space-y-2 rounded-xl border border-base-content/10 bg-base-200/50 p-3"
            aria-live="polite"
          >
            <h4 className="text-xs font-semibold tracking-wide text-base-content/60 uppercase">
              On ({enabledSources.length})
            </h4>
            {enabledSources.length === 0 ? (
              <p className="text-sm text-base-content/50">
                Nothing is on yet. Turn on a hub or collection below.
              </p>
            ) : (
              <ul className="flex flex-wrap gap-2">
                {enabledSources.map(({ source, libraryTitle }) => (
                  <li
                    key={persistedSourceKey(source.ServerId, source.Kind, source.Key)}
                    className="badge badge-primary badge-soft h-auto max-w-full gap-1 py-1 pr-1"
                  >
                    <span className="truncate font-medium">{source.Title}</span>
                    <span className="truncate text-base-content/60">· {libraryTitle}</span>
                    <Button
                      type="button"
                      size="xsmall"
                      variant="ghost"
                      className="btn-circle"
                      aria-label={`Turn off ${source.Title} in ${libraryTitle}`}
                      onClick={() => onChange(setPersistedSourceEnabled(settings, source, false))}
                    >
                      ×
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </div>
          {query.trim() ? (
            <div className="space-y-4">
              <p className="text-sm text-base-content/60">
                {matches.length === 0
                  ? `No hub or collection matches "${query.trim()}". Plex may name it differently; try fewer letters.`
                  : `${matchCount} ${matchCount === 1 ? "match" : "matches"} across ${matches.length} ${matches.length === 1 ? "library" : "libraries"}`}
              </p>
              {matches.map(({ library, sources: found }) => (
                <div
                  key={`${library.identity.libraryId}:${library.identity.type}`}
                  className="space-y-1"
                >
                  <h4 className="text-xs font-semibold tracking-wide text-base-content/60 uppercase">
                    {library.title} · {library.identity.type === "movie" ? "Movies" : "TV shows"}
                  </h4>
                  {found.map((source) => (
                    <PlexSourceRow
                      key={`${source.kind}:${source.key}`}
                      library={library}
                      source={source}
                      settings={settings}
                      onChange={onChange}
                      onCustomize={customizeSource}
                      onError={setError}
                      highlight={query}
                    />
                  ))}
                </div>
              ))}
            </div>
          ) : (
            <>
              <Tabs
                value={section}
                onChange={setChosenSection}
                options={[
                  { id: "movie", label: `Movies · ${sectionOn("movie")} on` },
                  { id: "show", label: `TV shows · ${sectionOn("show")} on` },
                ]}
              />
              {section === "movie" ? (
                <PlexMediaSection
                  key="movie"
                  type="movie"
                  title="Movies"
                  enabled={settings.MovieEnabled}
                  libraries={catalogue.movie}
                  settings={settings}
                  onChange={onChange}
                  onCustomize={customizeSource}
                  onError={setError}
                />
              ) : (
                <PlexMediaSection
                  key="show"
                  type="show"
                  title="TV shows"
                  enabled={settings.TvEnabled}
                  libraries={catalogue.show}
                  settings={settings}
                  onChange={onChange}
                  onCustomize={customizeSource}
                  onError={setError}
                />
              )}
            </>
          )}
        </div>
      )}
      {customizedSource && (
        <PlexSourceCustomization
          source={customizedSource}
          preview={preview?.key === customizing ? preview.items : null}
          busy={busy}
          onChange={updateSource}
          onPreview={() => void showPreview(customizedSource)}
          onClose={() => setCustomizing(null)}
        />
      )}
    </div>
  );
}
