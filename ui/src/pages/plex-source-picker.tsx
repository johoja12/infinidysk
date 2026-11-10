import { useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import { Link } from "react-router";
import { api, body, type Schemas } from "../api/client";
import { Empty, Failure, Field, Loading, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
type Source = Schemas["PlexPrefetchSource"];
type Resource = Schemas["PlexCatalogueSource"];
type SourceIdentity = Pick<Source, "server" | "viewer" | "kind" | "path">;
const same = (a: SourceIdentity, b: SourceIdentity) =>
  a.server === b.server &&
  a.viewer === b.viewer &&
  a.kind === b.kind &&
  a.path === b.path;

export function PlexSourcePicker({
  value,
  servers,
  accounts,
  disabled,
  onChange,
}: {
  value: Source[];
  servers: Schemas["PlexServer"][];
  accounts: Schemas["PlexAccount"][];
  disabled: boolean;
  onChange: (v: Source[]) => void;
}) {
  const [selectedServer, setServer] = useState("");
  const [viewer, setViewer] = useState("1");
  const [libraryId, setLibrary] = useState("home");
  const [search, setSearch] = useState("");
  const [custom, setCustom] = useState<Source | null>(null);
  const enabled = servers.filter((s) => s.enabled);
  const server = enabled.find((s) => s.id === selectedServer) ?? enabled[0];
  const libraries = useQuery({
    queryKey: ["plex-library", "source-libraries", server],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/plex/servers/{id}/libraries", {
          params: { path: { id: server!.id } },
          signal,
        }),
      ),
    enabled: !!server,
  });
  const catalogue = useQuery({
    queryKey: ["plex-library", "sources", server, viewer, libraryId],
    queryFn: async ({ signal }) => {
      const response = await api.GET("/api/v1/plex/servers/{id}/sources", {
        params: {
          path: { id: server!.id },
          query: {
            viewer,
            library: libraryId === "home" ? undefined : libraryId,
          },
        },
        signal,
      });
      const sources = await body(Promise.resolve(response));
      const encoded = response.response.headers.get("x-plex-viewer-name");
      return {
        sources,
        viewerName: encoded ? decodeURIComponent(encoded) : null,
      };
    },
    enabled: !!server,
  });
  const validation = useMutation({
    mutationFn: () =>
      body(
        api.POST("/api/v1/plex/sources/validate", { body: { sources: value } }),
      ),
  });
  const preview = useMutation({
    mutationFn: (source: Source) =>
      body(api.POST("/api/v1/plex/sources/preview", { body: source })),
  });
  const identity = (r: Resource): Source => ({
    server: server!.id,
    viewer,
    kind: r.kind,
    path: r.path,
    enabled: true,
    title: r.title,
    limit: 50,
    excluded_shows: [],
  });
  const set = (source: Source) => {
    const old = value.find((s) => same(s, source));
    onChange(
      old
        ? value.map((s) => (same(s, source) ? source : s))
        : [...value, source],
    );
  };
  const resourceLabel = (s: Source) =>
    s.title ||
    catalogue.data?.sources.find(
      (r) => r.kind === s.kind && r.path === s.path && s.server === server?.id,
    )?.title ||
    `Saved ${s.kind} source`;
  const customize = (s: Source) => {
    setCustom(s);
    preview.reset();
  };
  const resources =
    catalogue.data?.sources.filter((r) =>
      r.title.toLowerCase().includes(search.toLowerCase()),
    ) ?? [];
  if (!server)
    return (
      <Empty title="Connect Plex to choose sources">
        <Link
          className="text-primary underline"
          to="/plex-library?tab=connections"
        >
          Connect a Plex account and enable a server
        </Link>
        . Saved selections are retained.
      </Empty>
    );
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Plex server">
          <Select
            value={server.id}
            onValueChange={(id) => {
              setServer(id);
              setLibrary("home");
              setCustom(null);
              setSearch("");
            }}
          >
            <SelectTrigger aria-label="Plex source server">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {enabled.map((s) => (
                <SelectItem key={s.id} value={s.id}>
                  {s.name || s.id}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </Field>
        <Field
          label="Source profile"
          hint="Lists and previews use this profile's verified access."
        >
          <Select
            value={viewer}
            onValueChange={(id) => {
              setViewer(id);
              setCustom(null);
            }}
          >
            <SelectTrigger aria-label="Source profile">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="1">
                {catalogue.data?.viewerName || "Server owner"}
              </SelectItem>
              {accounts
                .filter((a) => (a.numeric_id ?? a.id) !== "1")
                .map((a) => (
                  <SelectItem key={a.id} value={a.numeric_id ?? a.id}>
                    {a.name}
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>
        </Field>
      </div>
      <div className="flex flex-wrap gap-2">
        <Select
          value={libraryId}
          onValueChange={(id) => {
            setLibrary(id);
            setCustom(null);
            setSearch("");
          }}
        >
          <SelectTrigger className="w-full sm:w-64" aria-label="Source library">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="home">Home hubs</SelectItem>
            {libraries.data?.map((l) => (
              <SelectItem key={l.id} value={l.id}>
                {l.kind === "movie" ? "Movies" : "TV"} · {l.title}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          aria-label="Find Plex source"
          className="w-full min-w-0 sm:flex-1 sm:max-w-sm"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Find hubs and collections"
        />
        <Button
          type="button"
          variant="outline"
          disabled={catalogue.isFetching}
          onClick={() => void catalogue.refetch()}
        >
          Refresh Plex catalogue
        </Button>
      </div>
      {libraries.error && <Failure error={libraries.error} />}
      <p className="text-xs text-muted-foreground">
        {value.filter((s) => s.enabled).length} enabled selections across all
        servers. Refreshing does not remove saved selections.
      </p>
      {catalogue.error && <Failure error={catalogue.error} />}
      {catalogue.isPending ? (
        <Loading />
      ) : (
        <div className="space-y-4">
          {(["hub", "collection", "library"] as const).map((kind) => {
            const rows = resources.filter((r) => r.kind === kind);
            return (
              rows.length > 0 && (
                <section key={kind} className="space-y-1">
                  <h3 className="text-xs font-medium text-muted-foreground">
                    {kind === "hub"
                      ? "Hubs"
                      : kind === "collection"
                        ? "Collections"
                        : "Whole library"}
                  </h3>
                  {rows.map((r) => {
                    const id = identity(r);
                    const saved = value.find((s) => same(s, id));
                    return (
                      <div
                        key={`${r.kind}:${r.path}`}
                        className="flex min-w-0 flex-wrap items-center gap-2 rounded-md border p-3"
                      >
                        <div className="min-w-0 basis-full sm:basis-auto sm:flex-1">
                          <Toggle
                            id={`source-${kind}-${encodeURIComponent(r.path)}`}
                            label={r.title}
                            checked={saved?.enabled ?? false}
                            disabled={disabled}
                            onChange={(checked) =>
                              set({
                                ...(saved ?? id),
                                title: r.title,
                                enabled: checked,
                              })
                            }
                          />
                        </div>
                        <span className="text-xs text-muted-foreground">
                          {saved?.limit ?? 50} items
                        </span>
                        <Button
                          type="button"
                          size="sm"
                          variant="ghost"
                          disabled={disabled}
                          onClick={() =>
                            customize(
                              saved
                                ? { ...saved, title: r.title }
                                : { ...id, enabled: false },
                            )
                          }
                        >
                          Customize
                        </Button>
                      </div>
                    );
                  })}
                </section>
              )
            );
          })}
          {!catalogue.error && resources.length === 0 && (
            <p className="text-sm text-muted-foreground">
              No sources match this selection.
            </p>
          )}
        </div>
      )}
      {custom && (
        <section
          className="space-y-3 rounded-lg border bg-muted/30 p-4"
          aria-label="Customize warming source"
        >
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-sm font-medium">
              Customize {resourceLabel(custom)}
            </h3>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setCustom(null)}
            >
              Close
            </Button>
          </div>
          <Field
            label="Item limit"
            hint="Warm an ordered prefix, from 1 to 1000 candidates."
          >
            <Input
              aria-label="Source item limit"
              type="number"
              min={1}
              max={1000}
              disabled={disabled}
              value={custom.limit}
              onChange={(e) => {
                const next = { ...custom, limit: Number(e.target.value) };
                setCustom(next);
                set(next);
              }}
            />
          </Field>
          <Field
            label="Excluded show IDs"
            hint="Only these exact shows are excluded from this source."
          >
            <Input
              aria-label="Source excluded shows"
              disabled={disabled}
              value={custom.excluded_shows.join(", ")}
              onChange={(e) => {
                const next = {
                  ...custom,
                  excluded_shows: e.target.value
                    .split(/[\s,;]+/)
                    .filter(Boolean),
                };
                setCustom(next);
                set(next);
              }}
            />
          </Field>
          <Button
            type="button"
            variant="outline"
            disabled={preview.isPending}
            onClick={() => preview.mutate(custom)}
          >
            Preview candidates
          </Button>
          <p className="text-xs text-muted-foreground">
            Preview reads Plex metadata only. It does not download or queue
            files and shows at most 50 candidates.
          </p>
          {preview.error && <Failure error={preview.error} />}
          <ul className="max-h-64 space-y-1 overflow-y-auto text-sm">
            {preview.data?.map((m) => (
              <li key={m.rating_key}>
                {m.title}
                {m.show_title && ` · ${m.show_title}`}
              </li>
            ))}
          </ul>
        </section>
      )}
      {value.length > 0 && (
        <details className="rounded-lg border p-3">
          <summary className="cursor-pointer text-sm font-medium">
            Saved selections ({value.length})
          </summary>
          <p className="my-2 text-xs text-muted-foreground">
            Saved selections include sources from other libraries and old Plex
            paths. Check availability to distinguish an empty hub from a path
            Plex no longer exposes. Checks never enable sources or download
            media.
          </p>
          <Button
            type="button"
            size="sm"
            variant="outline"
            disabled={validation.isPending || !value.length}
            onClick={() => validation.mutate()}
          >
            {validation.isPending
              ? "Checking saved sources…"
              : "Check saved sources"}
          </Button>
          {validation.error && <Failure error={validation.error} />}
          <div className="space-y-2">
            {value.map((s, index) => (
              <div
                key={`${s.server}:${s.viewer}:${s.kind}:${s.path}`}
                className="flex flex-wrap items-center gap-2 border-t py-2"
              >
                <div className="min-w-0 basis-full sm:basis-auto sm:flex-1">
                  <Toggle
                    id={`saved-source-${index}`}
                    label={`${resourceLabel(s)} · ${servers.find((v) => v.id === s.server)?.name ?? "Unavailable server"}`}
                    checked={s.enabled}
                    disabled={disabled}
                    onChange={(checked) => set({ ...s, enabled: checked })}
                  />
                </div>
                {validation.data
                  ?.filter((v) => same(v, s))
                  .map((v) => (
                    <p
                      key={v.path}
                      role="status"
                      className={`basis-full text-xs ${v.status === "missing" ? "text-warning-text" : "text-muted-foreground"}`}
                    >
                      {v.viewer_name && <>{v.viewer_name} · </>}
                      {v.message}
                    </p>
                  ))}
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  disabled={disabled}
                  onClick={() => customize(s)}
                >
                  Customize
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="ghost"
                  disabled={disabled}
                  onClick={() => {
                    onChange(value.filter((_, i) => i !== index));
                    if (custom && same(custom, s)) setCustom(null);
                  }}
                >
                  Remove selection
                </Button>
              </div>
            ))}
          </div>
        </details>
      )}
    </div>
  );
}
