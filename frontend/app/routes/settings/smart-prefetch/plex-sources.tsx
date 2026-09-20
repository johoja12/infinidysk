import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Button, Input, Select, Toggle } from "~/components/ui";
import {
  loadPlexBootstrap,
  plexRequest,
  type PlexLibrary,
  type PlexMedia,
  type PlexServer,
  type PlexSnapshot,
  type PlexSource,
  type PlexUser,
} from "../plex/plex-api";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";

function SelectedSource({
  source,
  update,
  remove,
  preview,
}: {
  source: PrefetchSource;
  update: (source: PrefetchSource) => void;
  remove: () => void;
  preview: () => void;
}) {
  const [excluded, setExcluded] = useState(source.ExcludedShows.join(", "));
  return (
    <div className="space-y-2 rounded border border-base-content/15 p-3">
      <p>
        {source.Title} · {source.Kind} · {source.ServerId} / {source.LibraryId || "global"}
      </p>
      <Toggle
        label={`Enable source ${source.Title}`}
        checked={source.Enabled}
        onChange={(event) => update({ ...source, Enabled: event.target.checked })}
      />
      <label>
        Item limit for {source.Title}
        <Input
          aria-label={`Item limit for ${source.Title}`}
          type="number"
          min={1}
          max={1000}
          value={source.Limit}
          onChange={(event) => update({ ...source, Limit: Number(event.target.value) })}
        />
      </label>
      {["show", "episode"].includes(source.Type) && (
        <label>
          Excluded show IDs for {source.Title}
          <Input
            aria-label={`Excluded show IDs for ${source.Title}`}
            value={excluded}
            onChange={(event) => setExcluded(event.target.value)}
            onBlur={() =>
              update({
                ...source,
                ExcludedShows: [...new Set(excluded.split(/[\s,;]+/).filter(Boolean))],
              })
            }
          />
        </label>
      )}
      <div className="flex gap-2">
        <Button type="button" onClick={preview}>
          Preview {source.Title}
        </Button>
        <Button type="button" onClick={remove}>
          Remove source {source.Title}
        </Button>
      </div>
    </div>
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
  const [libraryId, setLibraryId] = useState("");
  const [libraries, setLibraries] = useState<PlexSnapshot<PlexLibrary> | null>(null);
  const [users, setUsers] = useState<PlexSnapshot<PlexUser> | null>(null);
  const [sources, setSources] = useState<PlexSnapshot<PlexSource> | null>(null);
  const [preview, setPreview] = useState<{ title: string; items: PlexMedia[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
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
  const refresh = useCallback(
    async (forceRefresh = false) => {
      if (!serverId) return;
      const version = ++generation.current;
      setBusy(true);
      setError(null);
      try {
        const body = { serverId, libraryId: libraryId || null, forceRefresh };
        const [nextLibraries, nextUsers, nextSources] = await Promise.all([
          plexRequest<PlexSnapshot<PlexLibrary>>("libraries", body),
          plexRequest<PlexSnapshot<PlexUser>>("users", body),
          plexRequest<PlexSnapshot<PlexSource>>("sources", body),
        ]);
        if (version === generation.current) {
          setLibraries(nextLibraries);
          setUsers(nextUsers);
          setSources(nextSources);
        }
      } catch (cause) {
        if (version === generation.current)
          setError(cause instanceof Error ? cause.message : "Plex catalogue unavailable.");
      } finally {
        if (version === generation.current) setBusy(false);
      }
    },
    [serverId, libraryId],
  );
  useEffect(() => {
    const requestGeneration = generation;
    setSources(null);
    setPreview(null);
    void refresh();
    return () => {
      requestGeneration.current++;
    };
  }, [refresh]);
  const showPreview = async (source: PrefetchSource) => {
    setBusy(true);
    setError(null);
    try {
      const result = await plexRequest<{ items: PlexMedia[] }>("preview", {
        serverId: source.ServerId,
        key: source.Key,
        limit: Math.min(source.Limit, 20),
      });
      setPreview({ title: source.Title, items: result.items });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Preview unavailable.");
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="space-y-3">
      {error && <Alert variant="danger">{error}</Alert>}
      <p className="text-xs">
        Discover sources first; only sources you explicitly add become configured. Refreshed
        catalogues never overwrite your saved selections.
      </p>
      <label>
        Plex source server
        <Select
          aria-label="Plex source server"
          value={serverId}
          onChange={(event) => {
            setServerId(event.target.value);
            setLibraryId("");
            setLibraries(null);
            setUsers(null);
          }}
        >
          <option value="">Choose server</option>
          {servers.map((server) => (
            <option key={server.id} value={server.id}>
              {server.name}
              {server.enabled ? "" : " (disabled)"}
            </option>
          ))}
        </Select>
      </label>
      <label>
        Plex source library
        <Select
          aria-label="Plex source library"
          value={libraryId}
          disabled={!serverId}
          onChange={(event) => setLibraryId(event.target.value)}
        >
          <option value="">Global hubs</option>
          {libraries?.data.map((library) => (
            <option key={library.id} value={library.id}>
              {library.title} ({library.type})
            </option>
          ))}
        </Select>
      </label>
      <Button type="button" disabled={busy || !serverId} onClick={() => void refresh(true)}>
        Refresh Plex catalogue
      </Button>
      {[libraries, users, sources]
        .filter((snapshot) => snapshot !== null)
        .map((snapshot, index) => (
          <p key={index} className="text-xs">
            {["Libraries", "Users", "Sources"][index]}: {snapshot.isStale ? "Stale — " : "Updated "}
            {snapshot.lastSuccess
              ? new Date(snapshot.lastSuccess).toLocaleString()
              : "not yet available"}
            {snapshot.error ? ` · ${snapshot.error}` : ""}
          </p>
        ))}
      <p className="text-xs">
        History users also inform show-source predictions on this server. Without a matching
        selection, sources use the connected server account. Connect each selected Home account to
        verify its unwatched episodes; otherwise predictions are chronological with unknown watch
        status.
      </p>
      <div className="flex flex-wrap gap-3">
        {users?.data.map((user) => {
          const id = `${serverId}:${user.id}`;
          return (
            <Toggle
              key={id}
              label={`History user ${user.name}`}
              checked={settings.Users.includes(id)}
              onChange={(event) =>
                onChange({
                  ...settings,
                  Users: event.target.checked
                    ? [...settings.Users, id]
                    : settings.Users.filter((item) => item !== id),
                })
              }
            />
          );
        })}
      </div>
      <p className="text-xs">
        No selected users means all users. Selections are scoped by server. Currently selected:{" "}
        {settings.Users.join(", ") || "all"}.
      </p>
      {sources?.data.map((source) => {
        const mediaType =
          source.kind === "collection" && source.type === "collection"
            ? (libraries?.data.find((library) => library.id === source.libraryId)?.type ??
              source.type)
            : source.type;
        const selected = settings.Sources.some(
          (item) =>
            item.ServerId === source.serverId &&
            item.Kind === source.kind &&
            item.Key === source.key,
        );
        return (
          <div key={`${source.kind}:${source.key}`} className="flex flex-wrap items-center gap-2">
            <span>
              {source.title} ({source.kind}, {mediaType})
            </span>
            <Button
              type="button"
              disabled={
                busy ||
                selected ||
                settings.Sources.length >= 128 ||
                !["movie", "show", "episode", "clip"].includes(mediaType)
              }
              onClick={() =>
                onChange({
                  ...settings,
                  Sources: [
                    ...settings.Sources,
                    {
                      ServerId: source.serverId,
                      LibraryId: source.libraryId ?? "",
                      Kind: source.kind,
                      Key: source.key,
                      Title: source.title.slice(0, 512),
                      Type: mediaType,
                      Enabled: true,
                      Limit: 10,
                      ExcludedShows: [],
                    },
                  ],
                })
              }
            >
              Add source {source.title}
            </Button>
          </div>
        );
      })}
      {settings.Sources.map((source, index) => (
        <SelectedSource
          key={`${source.ServerId}:${source.Kind}:${source.Key}`}
          source={source}
          update={(next) =>
            onChange({
              ...settings,
              Sources: settings.Sources.map((item, position) => (position === index ? next : item)),
            })
          }
          remove={() =>
            onChange({
              ...settings,
              Sources: settings.Sources.filter((_, position) => position !== index),
            })
          }
          preview={() => void showPreview(source)}
        />
      ))}
      {preview && (
        <div className="rounded border border-base-content/15 p-3">
          <p>Preview: {preview.title} (up to 20 items)</p>
          {preview.items.length === 0 && <p>No members returned.</p>}
          {preview.items.map((item, index) => (
            <div key={`${item.ratingKey}:${index}`}>
              <p>{item.title}</p>
              {item.mappingStatus && (
                <p className="text-xs">
                  {item.mappingStatus} — {item.mappingReason}
                </p>
              )}
              <small>
                {item.file
                  ? `Plex path: ${item.file}; exact imported-item mapping is checked when warming.`
                  : "No file path returned; not yet mapped for warming."}
              </small>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
