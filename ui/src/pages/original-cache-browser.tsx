import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes } from "../format";
import { Failure, Loading } from "../ui";
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
import {
  AlertDialog,
  AlertDialogContent,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogCancel,
  AlertDialogAction,
} from "@/components/ui/alert-dialog";
import { CacheRangeMap, CacheMeter } from "./cache-range-map";
export type CacheEntry = Schemas["LegacyCacheInventoryEntry"];
type Sort = "recent" | "name" | "coverage" | "key";
type Coverage = "cached" | "all" | "complete" | "partial" | "empty" | "held";
export const identity = ({
  key,
  folder,
  item,
  size,
  generation,
  chunk,
}: CacheEntry): Schemas["LegacyCacheEntryIdentity"] => ({
  key,
  folder,
  item,
  size,
  generation,
  chunk,
});
export const retention = (e: CacheEntry) =>
  e.pinned === 1
    ? "Manual pin"
    : e.pinned === 2
      ? "Health protection"
      : e.pinned !== 0
        ? "Protected"
        : e.pending_bytes > 0 || e.dirty !== 0
          ? "Publication held"
          : "Unpinned";
const grid = "md:grid-cols-[minmax(0,3fr)_minmax(8rem,1.5fr)_8rem_9rem]";
export function CacheSelect({
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
    <select
      className="h-8 min-w-0 rounded-md border bg-background px-2 text-sm"
      aria-label={label}
      value={value}
      onChange={(e) => onChange(e.target.value)}
    >
      {options.map(([v, l]) => (
        <option key={v} value={v}>
          {l}
        </option>
      ))}
    </select>
  );
}
export function OriginalCacheBrowser() {
  const client = useQueryClient();
  const [params] = useSearchParams();
  const [cursors, setCursors] = useState([""]),
    [folder, setFolder] = useState("all"),
    [coverage, setCoverage] = useState<Coverage>(params.get("search") ? "all" : "cached"),
    [sort, setSort] = useState<Sort>("recent"),
    [text, setText] = useState(params.get("search") || ""),
    [search, setSearch] = useState(params.get("search") || "");
  const [selected, setSelected] = useState<CacheEntry | null>(null),
    [evict, setEvict] = useState<CacheEntry | null>(null),
    [rangePage, setRangePage] = useState(0);
  const reset = () => {
    setCursors([""]);
    setSelected(null);
    setRangePage(0);
  };
  useEffect(() => {
    if (text.trim() === search) return;
    const timer = setTimeout(() => {
      setSearch(text.trim());
      setCursors([""]);
      setSelected(null);
    }, 250);
    return () => clearTimeout(timer);
  }, [text, search]);
  const page = useQuery({
    queryKey: ["legacy-cache", cursors.at(-1), folder, search, coverage, sort],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/native-cache/legacy", {
          params: {
            query: {
              after: cursors.at(-1),
              folder: folder === "all" ? undefined : folder,
              search,
              sort,
              coverage,
            },
          },
          signal,
        }),
      ),
    refetchInterval: 15000,
  });
  const held =
    !!selected && (selected.pending_bytes > 0 || selected.dirty !== 0);
  const ranges = useQuery({
    queryKey: ["legacy-cache-ranges", selected],
    queryFn: ({ signal }) =>
      body(
        api.POST("/api/v1/native-cache/legacy/coverage", {
          body: identity(selected!),
          signal,
        }),
      ),
    enabled: !!selected && !held,
  });
  const action = useMutation({
    mutationFn: ({
      entry,
      operation,
    }: {
      entry: CacheEntry;
      operation: Schemas["NativeCacheOperation"];
    }) =>
      body(
        api.POST("/api/v1/native-cache/legacy", {
          body: { entry: identity(entry), operation },
        }),
      ),
    onSuccess: async (changed) => {
      if (!changed)
        toast.info(
          "No change: the entry is active, protected, read only, or has changed. Refresh before retrying.",
        );
      else toast.success("Cache entry updated");
      await client.invalidateQueries({ queryKey: ["legacy-cache"] });
      await client.invalidateQueries({ queryKey: ["legacy-cache-overview"] });
    },
    onError: (e) => toast.error(e.message),
  });
  const folders = page.data?.folders ?? [];
  const open = (entry: CacheEntry) => {
    setSelected(entry);
    setRangePage(0);
  };
  return (
    <section aria-label="Cached files" className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h2 className="text-lg font-medium">Cached files</h2>
          <p className="mt-1 text-xs text-muted-foreground">
            Inspect catalogue coverage, gaps and retention. Browsing does not
            download media.
          </p>
        </div>
        <p className="text-xs text-muted-foreground">
          {page.data?.matched_files?.toLocaleString() ?? "…"} matching
        </p>
      </div>
      <form
        className="flex flex-wrap gap-2 rounded-lg border bg-card p-3"
        onSubmit={(e) => {
          e.preventDefault();
          setSearch(text.trim());
          reset();
        }}
      >
        <Input
          type="search"
          aria-label="Search cached files"
          placeholder="Search file name or ID…"
          className="min-w-40 flex-1"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <CacheSelect
          label="Filter folder"
          value={folder}
          options={[
            ["all", "All folders"],
            ...folders.map((f) => [f.id, f.id] as [string, string]),
          ]}
          onChange={(v) => {
            setFolder(v);
            reset();
          }}
        />
        <CacheSelect
          label="Filter coverage"
          value={coverage}
          options={[
            ["cached", "Any cached"],
            ["complete", "Complete"],
            ["partial", "Partial"],
            ["empty", "Empty catalogue rows"],
            ["held", "Protected or held"],
            ["all", "All catalogue rows"],
          ]}
          onChange={(v) => {
            setCoverage(v as Coverage);
            reset();
          }}
        />
        <CacheSelect
          label="Sort cached files"
          value={sort}
          options={[
            ["recent", "Recently accessed"],
            ["name", "Name A–Z"],
            ["coverage", "Coverage high–low"],
          ]}
          onChange={(v) => {
            setSort(v as Sort);
            reset();
          }}
        />
        <Button type="submit" size="sm" variant="outline">
          Search
        </Button>
        <Button
          type="button"
          size="sm"
          variant="ghost"
          onClick={() => {
            setText("");
            setSearch("");
            setFolder("all");
            setCoverage("cached");
            setSort("recent");
            reset();
          }}
        >
          Clear filters
        </Button>
      </form>
      {page.error && <Failure error={page.error} />}{" "}
      {page.isPending ? (
        <Loading />
      ) : (
        <div
          role="table"
          aria-label="Cached files"
          className="overflow-hidden rounded-lg border bg-card"
        >
          <div
            role="row"
            className={`${grid} hidden gap-4 border-b px-4 py-2 text-xs font-medium text-muted-foreground md:grid`}
          >
            <span role="columnheader">File / retention</span>
            <span role="columnheader">Verified coverage</span>
            <span role="columnheader">Last access / allocated</span>
            <span role="columnheader">Actions</span>
          </div>
          {page.data?.entries.map((e) => {
            const folder = folders.find((f) => f.id === e.folder);
            const unsettled = e.pending_bytes > 0 || e.dirty !== 0;
            const protectedPin = e.pinned !== 0 && e.pinned !== 1;
            const canEvict =
              !action.isPending &&
              e.pinned === 0 &&
              !unsettled &&
              !page.data!.recency_pending &&
              !page.data!.recency_dropped &&
              !!folder?.eviction_enabled;
            return (
              <div
                role="row"
                aria-label={e.name || e.item}
                key={e.key}
                className={`${grid} grid gap-4 border-b p-4 last:border-0`}
              >
                <div role="cell" className="min-w-0">
                  <button
                    type="button"
                    title={e.name || e.item}
                    className="block max-w-full truncate text-left text-sm font-medium hover:text-primary"
                    onClick={() => open(e)}
                  >
                    {e.name || e.item}
                  </button>
                  <div className="mt-2 flex flex-wrap gap-1">
                    <Badge variant="secondary">{e.folder}</Badge>
                    <Badge variant={protectedPin ? "outline" : "secondary"}>
                      {retention(e)}
                    </Badge>
                  </div>
                  {!e.source_present && (
                    <p className="mt-2 text-xs text-muted-foreground">
                      No current imported source
                    </p>
                  )}
                </div>
                <div role="cell" className="space-y-2 text-xs tabular-nums">
                  <p>
                    {bytes(e.cached_bytes)}{" "}
                    <span className="text-muted-foreground">
                      / {bytes(e.size)}
                    </span>{" "}
                    · {Math.round((e.cached_bytes / Math.max(e.size, 1)) * 100)}
                    %
                  </p>
                  <CacheMeter
                    percent={(e.cached_bytes / Math.max(e.size, 1)) * 100}
                    label={`Coverage for ${e.name || e.item}`}
                  />
                  {unsettled && (
                    <p className="text-warning-text">
                      {bytes(e.pending_bytes)} pending · publication held
                    </p>
                  )}
                </div>
                <div
                  role="cell"
                  className="space-y-1 text-xs text-muted-foreground"
                >
                  <p>{ago(e.last_used_ms)}</p>
                  <p>{bytes(e.allocated_bytes)} allocated</p>
                </div>
                <div role="cell" className="flex flex-wrap items-start gap-1">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    onClick={() => open(e)}
                  >
                    Ranges
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={
                      !folder?.writable ||
                      action.isPending ||
                      protectedPin ||
                      unsettled
                    }
                    onClick={() =>
                      action.mutate({
                        entry: e,
                        operation: e.pinned === 1 ? "unpin" : "pin",
                      })
                    }
                  >
                    {e.pinned === 1 ? "Unpin" : "Pin"}
                  </Button>
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    disabled={!canEvict}
                    onClick={() => setEvict(e)}
                  >
                    Evict
                  </Button>
                </div>
              </div>
            );
          })}
          {page.data?.entries.length === 0 && (
            <div className="p-8 text-center text-sm text-muted-foreground">
              No cached files match these filters.
            </div>
          )}
        </div>
      )}
      <div className="flex flex-wrap items-center justify-between gap-3 text-xs text-muted-foreground">
        <p>
          Page {cursors.length} · up to 50 files per page. Live catalogue
          updates may change ordering.
        </p>
        <div className="flex gap-2">
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={cursors.length === 1 || page.isFetching}
            onClick={() => {
              setCursors((c) => c.slice(0, -1));
              setSelected(null);
            }}
          >
            Previous
          </Button>
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={!page.data?.next || page.isFetching}
            onClick={() => {
              setCursors((c) => [...c, page.data!.next!]);
              setSelected(null);
            }}
          >
            Next
          </Button>
          <Button
            type="button"
            size="sm"
            variant="ghost"
            disabled={page.isFetching}
            onClick={() => {
              if (cursors.length > 1) reset();
              else void page.refetch();
            }}
          >
            Refresh files
          </Button>
        </div>
      </div>
      <Dialog
        open={!!selected}
        onOpenChange={(v) => {
          if (!v) setSelected(null);
        }}
      >
        <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>Cached ranges</DialogTitle>
            <DialogDescription className="break-words">
              {selected?.name || selected?.item}
            </DialogDescription>
          </DialogHeader>
          {selected && (
            <>
              <div className="grid gap-3 rounded-lg border bg-muted/30 p-3 text-xs sm:grid-cols-3">
                <p>
                  {bytes(selected.cached_bytes)} covered /{" "}
                  {bytes(selected.size)}
                </p>
                <p>{bytes(selected.allocated_bytes)} allocated</p>
                <p>{retention(selected)}</p>
              </div>
              {held ? (
                <p className="text-sm text-warning-text">
                  Publication is unsettled. Coverage and verification are held
                  until it resolves.
                </p>
              ) : ranges.isPending ? (
                <Loading />
              ) : ranges.error ? (
                <Failure error={ranges.error} />
              ) : (
                <>
                  <CacheRangeMap
                    size={selected.size}
                    ranges={ranges.data ?? []}
                  />
                  <div className="max-h-48 overflow-auto rounded-md border">
                    <table className="w-full text-left text-xs tabular-nums">
                      <thead>
                        <tr>
                          <th className="p-2">Start</th>
                          <th className="p-2">End (exclusive)</th>
                          <th className="p-2">Length</th>
                        </tr>
                      </thead>
                      <tbody>
                        {ranges.data
                          ?.slice(rangePage * 100, (rangePage + 1) * 100)
                          .map((r) => (
                            <tr className="border-t" key={r.start}>
                              <td className="p-2">
                                {r.start.toLocaleString()}
                              </td>
                              <td className="p-2">{r.end.toLocaleString()}</td>
                              <td className="p-2">{bytes(r.end - r.start)}</td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </div>
                  {(ranges.data?.length ?? 0) > 100 && (
                    <div className="flex gap-2">
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={!rangePage}
                        onClick={() => setRangePage((p) => p - 1)}
                      >
                        Previous ranges
                      </Button>
                      <Button
                        size="sm"
                        variant="outline"
                        disabled={
                          (rangePage + 1) * 100 >= (ranges.data?.length ?? 0)
                        }
                        onClick={() => setRangePage((p) => p + 1)}
                      >
                        Next ranges
                      </Button>
                    </div>
                  )}
                </>
              )}
              <p className="text-xs text-muted-foreground">
                Catalogue coverage is not a full payload scrub. Playback
                verifies block hashes; the sample check reads at most one 4.2 MB
                block without provider traffic.
              </p>
              {!held && (
                <OriginalCacheProbe
                  key={selected.key}
                  entry={selected}
                  offset={ranges.data?.[0]?.start}
                />
              )}
              <details className="text-xs text-muted-foreground">
                <summary className="cursor-pointer">
                  Source and storage identity
                </summary>
                <div className="mt-2 space-y-2 break-all">
                  <p>Folder: {selected.folder}</p>
                  <p>Key: {selected.key}</p>
                  <p>Generation: {selected.generation ?? "Not recorded"}</p>
                  <p>File: {selected.item}</p>
                  {selected.source_present && (
                    <Link
                      className="text-primary hover:underline"
                      to={`/files/${selected.item}`}
                    >
                      Open imported file
                    </Link>
                  )}
                </div>
              </details>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => setSelected(null)}
              >
                Close ranges
              </Button>
            </>
          )}
        </DialogContent>
      </Dialog>
      <AlertDialog
        open={!!evict}
        onOpenChange={(v) => {
          if (!v) setEvict(null);
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Evict cached file?</AlertDialogTitle>
            <AlertDialogDescription>
              Removes this exact cached copy. Future reads require its provider
              source. Active files, pins and health protection prevent deletion.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (evict) action.mutate({ entry: evict, operation: "evict" });
                setEvict(null);
              }}
            >
              Evict cached file
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  );
}
function OriginalCacheProbe({
  entry,
  offset,
}: {
  entry: CacheEntry;
  offset?: number;
}) {
  const probe = useMutation({
    mutationFn: () =>
      body(
        api.POST("/api/v1/native-cache/legacy/probe", {
          body: { entry: identity(entry), offset: offset! },
        }),
      ),
  });
  return (
    <div className="space-y-2">
      <Button
        type="button"
        size="sm"
        variant="outline"
        disabled={offset == null || probe.isPending}
        onClick={() => probe.mutate()}
      >
        {probe.isPending ? "Verifying…" : "Verify cached sample"}
      </Button>
      {probe.error && <Failure error={probe.error} />}{" "}
      {probe.isSuccess && (
        <p role="status" className="text-sm">
          {probe.data != null
            ? `${bytes(probe.data)} verified against the catalogue hash`
            : "No committed block at this offset"}
        </p>
      )}
    </div>
  );
}
