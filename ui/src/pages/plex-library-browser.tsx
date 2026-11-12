import { libraryHref } from "../library-navigation";
import { useEffect, useState } from "react";
import { useSearchParams, Link } from "react-router";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Film, Tv, RefreshCw, Play, FolderOpen } from "lucide-react";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { useSettings } from "../settings";
import { ago, bytes } from "../format";
import { Loading, Failure, Empty } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from "@/components/ui/dialog";
import { CacheMeter, CacheRangeMap } from "./cache-range-map";
import { Player } from "./player";
import { copyText } from "../settings";
import { PlexClassification } from "./library-plex";
type File = Schemas["PlexBrowseFile"];
const columns =
  "lg:grid-cols-[minmax(0,2.4fr)_minmax(0,1.5fr)_4rem_4rem_5rem_7rem_7rem_5rem]";
function Choice({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: [string, string][];
  onChange: (v: string) => void;
}) {
  return (
    <label className="space-y-1 text-xs text-muted-foreground">
      <span className="block">{label}</span>
      <select
        aria-label={label}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="h-9 max-w-full rounded-md border bg-background px-2 text-sm text-foreground"
      >
        {options.map(([v, t]) => (
          <option value={v} key={v}>
            {t}
          </option>
        ))}
      </select>
    </label>
  );
}
const mappingLabels: Record<string, string> = {
  internal: "Imported source",
  external: "External file",
  broken: "Broken mapping",
  unknown: "Mapping not verified",
};
export function PlexLibraryBrowser() {
  const settings = useSettings(),
    client = useQueryClient();
  const [params, setParams] = useSearchParams();
  const servers =
    settings.data?.settings.plex.servers.filter((s) => s.enabled) ?? [];
  const server =
    servers.find((s) => s.id === params.get("server")) ?? servers[0];
  const library = params.get("library") || "all",
    view = params.get("view") || "groups",
    category = params.get("category") || "all",
    search = params.get("search") || "",
    group = params.get("group") || "";
  const [text, setText] = useState(search);
  useEffect(() => setText(search), [search]);
  const [inspect, setInspect] = useState<File | null>(null),
    [playing, setPlaying] = useState<Schemas["FileEntry"] | null>(null);
  const filters = {
    library,
    view,
    category,
    search,
    mapping: params.get("mapping") || "all",
    matched: params.get("matched") || "all",
    quality: params.get("quality") || "all",
    cache: params.get("cache") || "all",
    watched: params.get("watched") || "all",
    season: params.get("season") ? Number(params.get("season")) : undefined,
    group: group || undefined,
    sort: params.get("sort") || "title",
    offset: Math.max(0, Number(params.get("offset")) || 0),
    limit: Number(params.get("limit")) || 50,
  };
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
  const page = useQuery({
    queryKey: ["plex-library-browser", server?.id, filters],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/plex/servers/{id}/browse", {
          params: { path: { id: server!.id }, query: filters },
          signal,
        }),
      ),
    enabled: !!server,
    refetchInterval: (q) =>
      !q.state.data?.snapshot ||
      q.state.data.refreshing ||
      !q.state.data.coverage_ready
        ? 1000
        : 30000,
  });
  const refresh = useMutation({
    mutationFn: () =>
      body(
        api.GET("/api/v1/plex/servers/{id}/browse", {
          params: {
            path: { id: server!.id },
            query: { ...filters, refresh: true },
          },
        }),
      ),
    onSuccess: () =>
      void client.invalidateQueries({ queryKey: ["plex-library-browser"] }),
  });
  const navigate = (changes: Record<string, string>, resetGroup = true) => {
    const next = new URLSearchParams(params);
    next.delete("offset");
    if (resetGroup) next.delete("group");
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    setParams(next);
    setInspect(null);
  };
  const clear = () => {
    const next = new URLSearchParams();
    if (server) next.set("server", server.id);
    next.set("library", library);
    next.set("view", view);
    next.set("category", category);
    setText("");
    setParams(next);
  };
  const resolve = async (row: File) =>
    body(
      api.POST("/api/v1/plex/servers/{id}/browse/resolve", {
        params: { path: { id: server!.id } },
        body: { snapshot: page.data!.snapshot!, key: row.key },
      }),
    );
  const resolved = useQuery({
    queryKey: ["plex-library-file", page.data?.snapshot, inspect?.key],
    queryFn: () => resolve(inspect!),
    enabled: !!inspect && !!page.data?.snapshot,
    retry: false,
  });
  const details = useQuery({
    queryKey: ["file", resolved.data?.item],
    queryFn: () =>
      body(
        api.GET("/api/v1/files/{id}", {
          params: { path: { id: resolved.data!.item! } },
        }),
      ),
    enabled: !!resolved.data?.item,
  });
  const warm = useMutation({
    mutationFn: async () => {
      const current = await resolve(inspect!);
      if (!current.item)
        throw new Error(
          "No current imported source is available for this file",
        );
      return body(
        api.POST("/api/v1/prefetch", {
          body: {
            item: current.item,
            ranges: [],
            priority: 50,
            intent: crypto.randomUUID(),
          },
        }),
      );
    },
    onSuccess: () =>
      toast.success("Warming queued. Follow progress in Smart Prefetch."),
    onError: (e) => toast.error(e.message),
  });
  const play = useMutation({
    mutationFn: async () => {
      const current = await resolve(inspect!);
      if (!current.item) throw new Error("Open this external file in Plex");
      return body(
        api.GET("/api/v1/files/{id}", {
          params: { path: { id: current.item } },
        }),
      );
    },
    onSuccess: (d) => setPlaying(d.entry),
    onError: (e) => toast.error(e.message),
  });
  if (settings.isPending) return <Loading />;
  if (settings.error) return <Failure error={settings.error} />;
  if (!server)
    return (
      <Empty title="No enabled Plex servers">
        <Link
          to={libraryHref(params, "connections")}
          className="text-primary underline"
        >
          Connect Plex and select a server
        </Link>
      </Empty>
    );
  const data = page.data,
    ready = !!data?.snapshot,
    coverageWaiting = ready && filters.cache !== "all" && !data!.coverage_ready;
  const metric = (label: string, value: number, hint: string) => (
    <div className="min-w-0 rounded-lg border bg-card p-4">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-2 text-2xl font-medium tabular-nums">
        {value.toLocaleString()}
      </p>
      <p className="mt-1 text-xs text-muted-foreground">{hint}</p>
    </div>
  );
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end gap-3">
        <Choice
          label="Plex server"
          value={server.id}
          options={servers.map((s) => [s.id, s.name || s.id])}
          onChange={(v) =>
            navigate({ server: v, library: "all", category: "all" })
          }
        />
        <Choice
          label="Plex library"
          value={library}
          options={[
            ["all", "All libraries and indexed files"],
            ...(libraries.data ?? []).map(
              (l) => [l.id, l.title] as [string, string],
            ),
          ]}
          onChange={(v) => navigate({ library: v, category: "all" })}
        />
        <Button
          type="button"
          variant="outline"
          disabled={refresh.isPending || data?.refreshing}
          onClick={() => refresh.mutate()}
        >
          <RefreshCw className={data?.refreshing ? "animate-spin" : ""} />
          {data?.refreshing ? "Refreshing library…" : "Refresh library"}
        </Button>
        <span className="text-xs text-muted-foreground">
          {data?.updated_at
            ? `Updated ${ago(data.updated_at)}`
            : "Awaiting library catalogue"}
        </span>
      </div>
      {libraries.error && <Failure error={libraries.error} />}
      <PlexClassification />
      {data?.warning && (
        <p
          role="status"
          className="rounded-lg border p-3 text-sm text-warning-text"
        >
          {data.warning}
        </p>
      )}
      <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">
        {metric(
          "Indexed files",
          data?.totals.files ?? 0,
          "Across selected libraries",
        )}
        {metric(
          "Valid mappings",
          data?.totals.internal ?? 0,
          "Imported source available",
        )}
        {metric(
          "Need attention",
          data?.totals.attention ?? 0,
          "Broken or unverified mappings",
        )}
        {metric(
          "Unmatched",
          data?.totals.unmatched ?? 0,
          "Indexed files without Plex classification",
        )}
      </div>
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-2">
        <nav aria-label="Media type" className="flex flex-wrap gap-1">
          {(
            [
              ["all", "All media"],
              ["shows", "TV shows"],
              ["movies", "Movies"],
              ["unmatched", "Unmatched"],
            ] as const
          ).map(([v, t]) => (
            <Button
              size="sm"
              key={v}
              variant={category === v ? "secondary" : "ghost"}
              aria-pressed={category === v}
              onClick={() => navigate({ category: v })}
            >
              {t}
            </Button>
          ))}
        </nav>
        <nav aria-label="Library view" className="flex gap-1">
          {(
            [
              ["groups", "Grouped browse"],
              ["files", "File table"],
            ] as const
          ).map(([v, t]) => (
            <Button
              key={v}
              size="sm"
              variant={view === v ? "secondary" : "outline"}
              aria-pressed={view === v}
              onClick={() => navigate({ view: v })}
            >
              {t}
            </Button>
          ))}
        </nav>
      </div>
      <form
        className="flex flex-wrap items-end gap-3 rounded-lg border bg-card p-4"
        onSubmit={(e) => {
          e.preventDefault();
          navigate({ search: text });
        }}
      >
        <label className="min-w-44 flex-1 space-y-1 text-xs text-muted-foreground">
          Search media and paths
          <Input
            type="search"
            aria-label="Search media and paths"
            placeholder="Show, movie, file or library path…"
            value={text}
            onChange={(e) => setText(e.target.value)}
          />
        </label>
        <Choice
          label="Mapping"
          value={filters.mapping}
          options={[
            ["all", "All mappings"],
            ["internal", "Imported sources"],
            ["external", "External files"],
            ["broken", "Broken mappings"],
            ["unknown", "Not verified"],
          ]}
          onChange={(v) => navigate({ mapping: v })}
        />
        <Choice
          label="Match"
          value={filters.matched}
          options={[
            ["all", "All matches"],
            ["matched", "Plex matched"],
            ["unmatched", "Unmatched"],
          ]}
          onChange={(v) => navigate({ matched: v })}
        />
        <Choice
          label="Quality"
          value={filters.quality}
          options={[
            ["all", "All qualities"],
            ["4k", "4K"],
            ["1080p", "1080p"],
            ["720p", "720p"],
            ["sd", "SD"],
            ["unknown", "Unknown"],
          ]}
          onChange={(v) => navigate({ quality: v })}
        />
        <Choice
          label="Native Cache"
          value={filters.cache}
          options={[
            ["all", "All cache states"],
            ["any", "Has cached data"],
            ["complete", "Fully cached"],
            ["empty", "Not cached"],
            ["unavailable", "Unavailable"],
          ]}
          onChange={(v) => navigate({ cache: v })}
        />
        <Choice
          label="Watch status"
          value={filters.watched}
          options={[
            ["all", "All watch states"],
            ["watched", "Watched"],
            ["unwatched", "Unwatched"],
            ["in-progress", "In progress"],
          ]}
          onChange={(v) => navigate({ watched: v })}
        />
        <label className="space-y-1 text-xs text-muted-foreground">
          Season
          <Input
            aria-label="Season"
            className="w-20"
            type="number"
            min={0}
            max={1000}
            value={filters.season ?? ""}
            placeholder="Any"
            onChange={(e) => navigate({ season: e.target.value }, false)}
          />
        </label>
        <Choice
          label="Sort library"
          value={filters.sort}
          options={[
            ["title", "Title"],
            ["recent", "Recently watched"],
            ["size", "Largest files"],
            ["coverage", "Cached bytes"],
          ]}
          onChange={(v) => navigate({ sort: v }, false)}
        />
        <Button type="submit" variant="outline">
          Apply filters
        </Button>
        <Button type="button" variant="ghost" onClick={clear}>
          Clear filters
        </Button>
      </form>
      <p className="text-xs text-muted-foreground">
        Quality uses Plex metadata or filename tags. Cache coverage is checked
        for the current imported source; unavailable coverage stays unknown.
        {data && !data.coverage_ready && (
          <>
            {" "}
            Checking cache metadata: {data.coverage_checked.toLocaleString()} of{" "}
            {data.coverage_total.toLocaleString()} sources.
          </>
        )}
        {data?.coverage_at && (
          <> Cache metadata checked {ago(data.coverage_at)}.</>
        )}
      </p>
      {page.error ? (
        <Failure error={page.error} />
      ) : !ready || coverageWaiting ? (
        <div role="status">
          <Loading />
          {coverageWaiting
            ? "Checking complete catalogue coverage before applying this filter…"
            : "Loading complete library catalogue…"}
        </div>
      ) : (
        <>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              {group && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => navigate({ group: "", season: "" })}
                >
                  <FolderOpen />
                  All groups
                </Button>
              )}
              <span className="text-sm text-muted-foreground">
                {data!.matched_files.toLocaleString()} matching files
                {view === "groups" && !group
                  ? ` · ${data!.total_groups.toLocaleString()} groups`
                  : ""}
              </span>
            </div>
            <Choice
              label="Rows per page"
              value={String(filters.limit)}
              options={[
                ["25", "25"],
                ["50", "50"],
                ["100", "100"],
              ]}
              onChange={(v) => navigate({ limit: v }, false)}
            />
          </div>
          {view === "groups" && !group ? (
            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              {data!.groups.map((g) => (
                <article
                  key={g.key}
                  className="overflow-hidden rounded-lg border bg-card"
                >
                  <button
                    className="flex w-full items-start gap-4 p-4 text-left hover:bg-muted/30"
                    onClick={() =>
                      navigate({ group: g.key, season: "" }, false)
                    }
                    aria-label={`Open ${g.title}`}
                  >
                    <div className="relative flex h-28 w-20 shrink-0 items-center justify-center overflow-hidden rounded-md bg-muted text-muted-foreground">
                      {g.kind === "episode" ? <Tv /> : <Film />}
                      {g.artwork_key && (
                        <img
                          loading="lazy"
                          alt=""
                          className="absolute inset-0 size-full object-cover"
                          src={`/api/v1/plex/servers/${encodeURIComponent(server.id)}/artwork/${encodeURIComponent(g.artwork_key)}`}
                          onError={(e) => {
                            e.currentTarget.style.display = "none";
                          }}
                        />
                      )}
                    </div>
                    <div className="min-w-0 flex-1">
                      <h3 className="break-words font-medium">{g.title}</h3>
                      <p className="mt-1 text-xs text-muted-foreground">
                        {g.kind === "episode" ? "TV show" : (g.year ?? "Media")}{" "}
                        · {g.files} files
                        {g.seasons > 0 ? ` · ${g.seasons} seasons` : ""}
                      </p>
                      <div className="my-2 flex flex-wrap gap-1">
                        {g.qualities.map((q) => (
                          <Badge variant="outline" key={q}>
                            {q === "4k" ? "4K" : q}
                          </Badge>
                        ))}
                        {g.attention > 0 && (
                          <Badge variant="outline">
                            {g.attention} need attention
                          </Badge>
                        )}
                      </div>
                      <p className="text-xs tabular-nums text-muted-foreground">
                        {bytes(g.bytes)}
                        {g.cached_bytes != null
                          ? ` · ${bytes(g.cached_bytes)} cached`
                          : " · Coverage unavailable"}
                      </p>
                      {g.cached_bytes != null && g.bytes > 0 && (
                        <div className="mt-2">
                          <CacheMeter
                            percent={(g.cached_bytes / g.bytes) * 100}
                            label={`Coverage for ${g.title}`}
                          />
                        </div>
                      )}
                    </div>
                  </button>
                  <div className="border-t px-4 py-2 text-xs text-muted-foreground">
                    Browse files and seasons
                  </div>
                </article>
              ))}
            </div>
          ) : (
            <div
              role="table"
              aria-label="Library files"
              className="overflow-hidden rounded-lg border bg-card"
            >
              <div
                role="row"
                className={`hidden gap-3 border-b bg-muted/30 px-4 py-3 text-xs text-muted-foreground lg:grid ${columns}`}
              >
                {[
                  "File / path",
                  "Show / movie",
                  "Season",
                  "Episode",
                  "Quality",
                  "Size / mapping",
                  "Native Cache",
                  "Details",
                ].map((t) => (
                  <span role="columnheader" key={t}>
                    {t}
                  </span>
                ))}
              </div>
              {data!.files.map((f) => (
                <div
                  key={f.key}
                  role="row"
                  aria-label={f.media.title}
                  className={`grid min-w-0 gap-3 border-b p-4 last:border-0 ${columns}`}
                >
                  <div role="cell" className="min-w-0">
                    <button
                      className="w-full break-words text-left text-sm font-medium hover:text-primary"
                      onClick={() => setInspect(f)}
                    >
                      {f.media.file?.split(/[\\/]/).at(-1) || f.media.title}
                    </button>
                    <details className="mt-1 text-xs text-muted-foreground">
                      <summary className="cursor-pointer">File path</summary>
                      <p className="mt-1 break-all">
                        {f.media.file || "Not recorded"}
                      </p>
                    </details>
                  </div>
                  <div role="cell" className="min-w-0 break-words text-sm">
                    {f.media.show_title || f.media.title}
                    <p className="mt-1 text-xs text-muted-foreground">
                      {!f.matched
                        ? "Unmatched"
                        : (f.media.year ??
                          (f.media.kind === "episode" ? "TV" : "Movie"))}
                      {f.watched_known
                        ? ` · ${(f.media.view_count ?? 0) > 0 ? "Watched" : (f.media.view_offset_ms ?? 0) > 0 ? "In progress" : "Unwatched"}`
                        : " · Watch state unavailable"}
                    </p>
                  </div>
                  <div role="cell" className="text-sm tabular-nums">
                    <span className="text-xs text-muted-foreground lg:hidden">
                      Season{" "}
                    </span>
                    {f.media.season ?? "—"}
                  </div>
                  <div role="cell" className="text-sm tabular-nums">
                    <span className="text-xs text-muted-foreground lg:hidden">
                      Episode{" "}
                    </span>
                    {f.media.episode ?? "—"}
                  </div>
                  <div role="cell">
                    <Badge variant="outline">
                      {f.quality === "4k" ? "4K" : f.quality}
                    </Badge>
                  </div>
                  <div role="cell" className="text-sm tabular-nums">
                    {f.size == null ? "—" : bytes(f.size)}
                    <p className="mt-1 text-xs text-muted-foreground">
                      {mappingLabels[f.mapping] || f.mapping}
                    </p>
                  </div>
                  <div role="cell" className="space-y-1 text-xs tabular-nums">
                    {f.cached_bytes != null && f.size != null ? (
                      <>
                        <p>
                          {bytes(f.cached_bytes)} / {bytes(f.size)}
                        </p>
                        <CacheMeter
                          percent={
                            f.size > 0 ? (f.cached_bytes / f.size) * 100 : 0
                          }
                          label={`Coverage for ${f.media.title}`}
                        />
                      </>
                    ) : (
                      <span className="text-muted-foreground">
                        Coverage unavailable
                      </span>
                    )}
                  </div>
                  <div role="cell">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setInspect(f)}
                    >
                      Inspect file
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          )}
          {(view === "groups" && !group
            ? data!.groups.length
            : data!.files.length) === 0 && (
            <Empty title="No media matches these filters">
              Try another search, library or filter.
            </Empty>
          )}
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
            <p>
              Page {Math.floor(filters.offset / filters.limit) + 1} ·{" "}
              {filters.limit} per page
            </p>
            <div className="flex gap-2">
              <Button
                variant="outline"
                size="sm"
                disabled={filters.offset === 0 || page.isFetching}
                onClick={() =>
                  navigate(
                    {
                      offset: String(
                        Math.max(0, filters.offset - filters.limit),
                      ),
                    },
                    false,
                  )
                }
              >
                Previous
              </Button>
              <Button
                variant="outline"
                size="sm"
                disabled={data!.next == null || page.isFetching}
                onClick={() => navigate({ offset: String(data!.next) }, false)}
              >
                Next
              </Button>
            </div>
          </div>
        </>
      )}
      <Dialog
        open={!!inspect}
        onOpenChange={(open) => {
          if (!open) setInspect(null);
        }}
      >
        <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>{inspect?.media.title || "File details"}</DialogTitle>
            <DialogDescription>
              File mapping, playback and current cache coverage.
            </DialogDescription>
          </DialogHeader>
          {inspect && (
            <div className="space-y-4">
              <div className="flex flex-wrap gap-2">
                <Badge variant="outline">
                  {inspect.quality === "4k" ? "4K" : inspect.quality}
                </Badge>
                <Badge variant="outline">
                  {mappingLabels[inspect.mapping]}
                </Badge>
                <Badge variant="outline">
                  {inspect.media.kind === "episode"
                    ? `Season ${inspect.media.season ?? "?"} · Episode ${inspect.media.episode ?? "?"}`
                    : inspect.media.kind === "movie"
                      ? "Movie"
                      : "Unmatched file"}
                </Badge>
              </div>
              <div className="rounded-lg border bg-muted/20 p-3">
                <p className="break-all text-sm">
                  {inspect.media.file || "No file version recorded"}
                </p>
                <Button
                  size="sm"
                  variant="ghost"
                  onClick={() => {
                    if (inspect.media.file) void copyText(inspect.media.file);
                  }}
                >
                  Copy path
                </Button>
              </div>
              {resolved.isPending ? (
                <Loading />
              ) : resolved.error ? (
                <Failure error={resolved.error} />
              ) : !resolved.data?.item ? (
                <p className="text-sm text-muted-foreground">
                  No exact imported mapping. External files remain available in
                  Plex.
                </p>
              ) : details.isPending ? (
                <Loading />
              ) : details.error ? (
                <Failure error={details.error} />
              ) : (
                <>
                  <p className="text-sm">
                    {bytes(details.data?.entry.size ?? 0)} ·{" "}
                    {details.data?.cache_available
                      ? `${bytes(details.data.cached_ranges.reduce((n, r) => n + r.end - r.start, 0))} cached`
                      : "Cache unavailable"}
                  </p>
                  {details.data?.cache_available && (
                    <CacheRangeMap
                      size={details.data.entry.size ?? 0}
                      ranges={details.data.cached_ranges}
                    />
                  )}{" "}
                  {details.data?.cache_warning && (
                    <p className="text-sm text-warning-text">
                      {details.data.cache_warning}
                    </p>
                  )}
                  <div className="flex flex-wrap gap-2">
                    <Button
                      variant="outline"
                      disabled={play.isPending}
                      onClick={() => play.mutate()}
                    >
                      <Play />
                      Play file
                    </Button>
                    <Button
                      variant="outline"
                      disabled={warm.isPending}
                      onClick={() => warm.mutate()}
                    >
                      Warm file
                    </Button>
                    <Button variant="outline" asChild>
                      <Link to={`/files/${resolved.data.item}`}>
                        Source details
                      </Link>
                    </Button>
                    <Button variant="outline" asChild>
                      <Link
                        to={`/native-cache?tab=files&search=${encodeURIComponent(resolved.data.item)}`}
                      >
                        Manage cache
                      </Link>
                    </Button>
                  </div>
                </>
              )}
              {resolved.data?.media.rating_key &&
                !resolved.data.media.rating_key.startsWith("native:") && (
                  <a
                    target="_blank"
                    rel="noreferrer"
                    className="inline-block text-sm text-primary underline"
                    href={`https://app.plex.tv/desktop/#!/server/${encodeURIComponent(server.id)}/details?key=${encodeURIComponent(`/library/metadata/${resolved.data.media.rating_key}`)}`}
                  >
                    Open in Plex
                  </a>
                )}
              <details className="text-xs text-muted-foreground">
                <summary className="cursor-pointer">Record identity</summary>
                <p className="mt-2 break-all">{inspect.key}</p>
                <p>{resolved.data?.item || "No imported source"}</p>
              </details>
              <Button variant="outline" onClick={() => setInspect(null)}>
                Close file details
              </Button>
            </div>
          )}
        </DialogContent>
      </Dialog>
      {playing && <Player entry={playing} onClose={() => setPlaying(null)} />}
    </div>
  );
}
