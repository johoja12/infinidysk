import { useState } from "react";
import { Link, useSearchParams } from "react-router";
import { useMutation, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, body } from "../api/client";
import { useSettings } from "../settings";
import { bytes } from "../format";
import { Empty, Failure, Loading, PageHeader, SettingsPanel } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { PlexConnections } from "./plex";

export function PlexLibrary() {
  const [params, setParams] = useSearchParams();
  const connections = params.get("tab") === "connections";
  return (
    <>
      <PageHeader
        title="Plex Library"
        description="Browse Plex media and open its exact imported source."
      >
        <Button variant="outline" asChild>
          <Link to="/smart-prefetch">Smart Prefetch</Link>
        </Button>
      </PageHeader>
      <nav
        aria-label="Plex Library views"
        className="mb-6 flex flex-wrap gap-2"
      >
        <Button
          variant={!connections ? "secondary" : "ghost"}
          aria-current={!connections ? "page" : undefined}
          onClick={() => setParams({})}
        >
          Browse library
        </Button>
        <Button
          variant={connections ? "secondary" : "ghost"}
          aria-current={connections ? "page" : undefined}
          onClick={() => setParams({ tab: "connections" })}
        >
          Connections and path mappings
        </Button>
        <Button variant="ghost" asChild>
          <Link to="/library">Local media index</Link>
        </Button>
      </nav>
      {connections ? <PlexConnections /> : <PlexBrowser />}
    </>
  );
}

export function PlexBrowser() {
  const settings = useSettings();
  const [params, setParams] = useSearchParams();
  const [text, setText] = useState(params.get("search") ?? "");
  const [inspect, setInspect] = useState<string | null>(null);
  const servers =
    settings.data?.settings.plex.servers.filter((s) => s.enabled) ?? [];
  const server =
    servers.find((s) => s.id === params.get("server")) ?? servers[0];
  const libraries = useQuery({
    queryKey: ["plex-library", "libraries", server],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/plex/servers/{id}/libraries", {
          params: { path: { id: server!.id } },
          signal,
        }),
      ),
    enabled: !!server,
  });
  const library =
    libraries.data?.find((l) => l.id === params.get("library")) ??
    libraries.data?.[0];
  const parent = params.get("parent") ?? undefined;
  const search = params.get("search") ?? "";
  const parsed = Number(params.get("offset") ?? 0);
  const offset = Number.isInteger(parsed) && parsed >= 0 ? parsed : 0;
  const page = useQuery({
    queryKey: [
      "plex-library",
      "media",
      server,
      library?.id,
      parent,
      search,
      offset,
    ],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/plex/servers/{id}/libraries/{library}/media", {
          params: {
            path: { id: server!.id, library: library!.id },
            query: { parent, search, offset },
          },
          signal,
        }),
      ),
    enabled: !!server && !!library,
  });
  const details = useQuery({
    queryKey: ["file", inspect],
    queryFn: () =>
      body(
        api.GET("/api/v1/files/{id}", { params: { path: { id: inspect! } } }),
      ),
    enabled: !!inspect,
  });
  const warm = useMutation({
    mutationFn: (item: string) =>
      body(
        api.POST("/api/v1/prefetch", {
          body: { item, ranges: [], priority: 50, intent: crypto.randomUUID() },
        }),
      ),
    onSuccess: () =>
      toast.success("Warming queued. Follow progress in Smart Prefetch."),
    onError: (e) => toast.error(e.message),
  });
  const navigate = (change: Record<string, string>, clear = false) => {
    const next = clear ? new URLSearchParams() : new URLSearchParams(params);
    next.delete("offset");
    for (const [k, v] of Object.entries(change)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setInspect(null);
    setParams(next);
  };
  if (settings.isPending) return <Loading />;
  if (settings.error) return <Failure error={settings.error} />;
  if (!server)
    return (
      <Empty title="No enabled Plex servers">
        <Link
          className="text-primary underline"
          to="/plex-library?tab=connections"
        >
          Connect a Plex account and select a server
        </Link>{" "}
        to browse its libraries.
      </Empty>
    );
  return (
    <div className="space-y-4">
      <form
        className="flex flex-wrap gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          navigate({ search: text });
        }}
      >
        <Select
          value={server.id}
          onValueChange={(value) => {
            setText("");
            navigate({ server: value }, true);
          }}
        >
          <SelectTrigger aria-label="Plex server" className="w-full sm:w-56">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {servers.map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.name || s.id}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {library && (
          <Select
            value={library.id}
            onValueChange={(value) => {
              setText("");
              navigate({ server: server.id, library: value }, true);
            }}
          >
            <SelectTrigger aria-label="Plex library" className="w-full sm:w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {libraries.data?.map((l) => (
                <SelectItem key={l.id} value={l.id}>
                  {l.title}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <Input
          aria-label="Search Plex media"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Search Plex media"
          className="min-w-0 flex-1 sm:max-w-sm"
        />
        <Button type="submit" variant="outline">
          Search
        </Button>
        <Button
          type="button"
          variant="outline"
          disabled={page.isFetching}
          onClick={() => void page.refetch()}
        >
          Refresh
        </Button>
      </form>
      {libraries.isPending ? (
        <Loading />
      ) : libraries.error ? (
        <Failure error={libraries.error} />
      ) : !library ? (
        <Empty title="No movie or TV libraries available">
          Check library access in Plex connections.
        </Empty>
      ) : (
        <>
          {parent && (
            <Button
              variant="outline"
              size="sm"
              onClick={() => navigate({ parent: "", search: "" })}
            >
              Back to shows
            </Button>
          )}
          <p className="text-xs text-muted-foreground">
            {page.data?.total != null
              ? `${page.data.total.toLocaleString()} matching Plex records. `
              : ""}
            Only an exact mapped imported source can be warmed. Files stored
            outside infinidysk remain browseable.
          </p>
          {page.isPending ? (
            <Loading />
          ) : page.error ? (
            <Failure error={page.error} />
          ) : page.data?.files.length === 0 ? (
            <Empty title="No Plex media matches">
              Try another library or search.
            </Empty>
          ) : (
            <div className="overflow-x-auto rounded-lg border bg-card">
              <table className="w-full text-left text-sm">
                <thead className="text-muted-foreground">
                  <tr>
                    <th className="p-3">Media</th>
                    <th className="p-3">Source</th>
                    <th className="hidden p-3 sm:table-cell">Size</th>
                    <th className="p-3">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {page.data?.files.map((row) => (
                    <tr className="border-t" key={row.media.rating_key}>
                      <td className="max-w-64 p-3">
                        {row.media.kind === "show" ? (
                          <Button
                            variant="link"
                            className="h-auto max-w-full truncate px-0"
                            onClick={() =>
                              navigate({
                                server: server.id,
                                library: library.id,
                                parent: row.media.rating_key,
                                search: "",
                              })
                            }
                          >
                            {row.media.title}
                          </Button>
                        ) : (
                          <p className="truncate">{row.media.title}</p>
                        )}
                        {row.media.show_title && (
                          <p className="truncate text-xs text-muted-foreground">
                            {row.media.show_title} · S{row.media.season} E
                            {row.media.episode}
                          </p>
                        )}
                        <p className="text-xs text-muted-foreground">
                          {(row.media.view_count ?? 0) > 0
                            ? "Watched"
                            : "Unwatched"}
                          {row.media.view_offset_ms > 0 && " · In progress"}
                        </p>
                      </td>
                      <td className="p-3 text-xs">
                        {row.item
                          ? "Imported source"
                          : row.media.kind === "show"
                            ? "Show"
                            : "No exact imported mapping"}
                      </td>
                      <td className="hidden whitespace-nowrap p-3 sm:table-cell">
                        {row.size != null ? bytes(row.size) : "—"}
                      </td>
                      <td className="p-3">
                        <div className="flex flex-wrap gap-1">
                          {row.item && (
                            <>
                              <Button
                                variant="ghost"
                                size="sm"
                                onClick={() => setInspect(row.item!)}
                              >
                                Cache coverage
                              </Button>
                              <Button variant="ghost" size="sm" asChild>
                                <Link
                                  to={`/explorer?item=${encodeURIComponent(row.item)}`}
                                >
                                  File details
                                </Link>
                              </Button>
                              <Button
                                variant="outline"
                                size="sm"
                                disabled={
                                  warm.isPending ||
                                  !settings.data?.settings.prefetch.enabled ||
                                  !settings.data?.settings.native_cache.enabled
                                }
                                onClick={() => warm.mutate(row.item!)}
                              >
                                Warm file
                              </Button>
                            </>
                          )}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <div className="flex justify-end gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={offset === 0 || page.isFetching}
              onClick={() =>
                navigate({ offset: String(Math.max(0, offset - 50)) })
              }
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={page.data?.next == null || page.isFetching}
              onClick={() => navigate({ offset: String(page.data!.next) })}
            >
              Next
            </Button>
          </div>
        </>
      )}
      {inspect && (
        <SettingsPanel title="Current source cache coverage">
          {details.isPending ? (
            <Loading />
          ) : details.error ? (
            <Failure error={details.error} />
          ) : (
            <>
              <p className="text-sm">
                {details.data?.cache_available
                  ? `${bytes(details.data.cached_ranges.reduce((n, r) => n + r.end - r.start, 0))} cached`
                  : "Cache unavailable"}
              </p>
              <p className="break-all font-mono text-xs">
                {details.data?.cached_ranges
                  .map((r) => `${r.start}–${r.end}`)
                  .join(", ") || "No cached ranges"}
              </p>
              {details.data?.cache_warning && (
                <p className="text-warning-text text-sm">
                  {details.data.cache_warning}
                </p>
              )}
            </>
          )}
          <Button variant="outline" size="sm" onClick={() => setInspect(null)}>
            Close coverage
          </Button>
        </SettingsPanel>
      )}
    </div>
  );
}
