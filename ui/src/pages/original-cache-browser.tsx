import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes } from "../format";
import { Empty, Failure, Loading, SettingsPanel } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";

type Entry = Schemas["LegacyCacheInventoryEntry"];
const identity = ({
  key,
  folder,
  item,
  size,
  generation,
  chunk,
}: Entry): Schemas["LegacyCacheEntryIdentity"] => ({
  key,
  folder,
  item,
  size,
  generation,
  chunk,
});

export function OriginalCacheBrowser() {
  const client = useQueryClient();
  const [cursors, setCursors] = useState([""]);
  const [folder, setFolder] = useState("all");
  const [text, setText] = useState("");
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<Entry | null>(null);
  const [evict, setEvict] = useState<Entry | null>(null);
  const page = useQuery({
    queryKey: ["legacy-cache", cursors.at(-1), folder, search],
    queryFn: () =>
      body(
        api.GET("/api/v1/native-cache/legacy", {
          params: {
            query: {
              after: cursors.at(-1),
              folder: folder === "all" ? undefined : folder,
              search,
            },
          },
        }),
      ),
    refetchInterval: 15000,
  });
  const ranges = useQuery({
    queryKey: ["legacy-cache-ranges", selected],
    queryFn: () =>
      body(
        api.POST("/api/v1/native-cache/legacy/coverage", {
          body: identity(selected!),
        }),
      ),
    enabled: !!selected,
  });
  const action = useMutation({
    mutationFn: ({
      entry,
      operation,
    }: {
      entry: Entry;
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
    },
    onError: (e) => toast.error(e.message),
  });
  const folders = page.data?.folders ?? [];
  return (
    <section className="space-y-4" aria-label="Original cache storage">
      <h2 className="text-lg font-medium">Original cache storage</h2>
      <p className="text-sm text-muted-foreground">
        The same folders used by v1. Browsing reads metadata and does not copy,
        convert, or remove media.
      </p>
      {(page.data?.recency_pending ?? 0) > 0 && (
        <p role="status" className="text-warning-text text-sm">
          {page.data?.recency_pending} recency updates pending. Eviction waits
          until they settle.
        </p>
      )}
      {(page.data?.recency_dropped ?? 0) > 0 && (
        <p role="status" className="text-warning-text text-sm">
          Recency accounting is uncertain. Eviction remains held.
        </p>
      )}
      {page.isPending ? (
        <Loading />
      ) : page.error ? (
        <Failure error={page.error} />
      ) : (
        <>
          <div className="grid gap-3 sm:grid-cols-2">
            {folders.map((f) => (
              <div
                key={f.id}
                className="space-y-2 rounded-lg border bg-card p-4"
              >
                <h3 className="break-all text-sm font-medium">{f.id}</h3>
                <p className="break-all font-mono text-xs text-muted-foreground">
                  {f.path}
                </p>
                <p className="text-sm">
                  {f.files.toLocaleString()} files · {bytes(f.cached_bytes)}{" "}
                  catalogue coverage
                </p>
                <p className="text-xs text-muted-foreground">
                  {bytes(f.allocated_bytes)} accounted space
                  {f.quota_bytes != null && ` · quota ${bytes(f.quota_bytes)}`}
                  {f.min_free_bytes != null &&
                    ` · reserve ${bytes(f.min_free_bytes)}`}
                </p>
                <p className="text-xs text-muted-foreground">
                  {bytes(f.pending_bytes)} pending · {bytes(f.retired_bytes)}{" "}
                  retired · {f.held_files.toLocaleString()} files held from
                  eviction
                </p>
                <p className="text-xs">
                  {f.writable ? "Compatible writes enabled" : "Read only"} ·{" "}
                  {f.eviction_enabled
                    ? "Eviction enabled"
                    : "Eviction disabled"}
                </p>
              </div>
            ))}
          </div>
          <form
            className="flex flex-wrap gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              setSearch(text);
              setCursors([""]);
              setSelected(null);
            }}
          >
            <Input
              aria-label="Search cached files"
              placeholder="Search name or file ID"
              className="min-w-0 flex-1 sm:max-w-sm"
              value={text}
              onChange={(e) => setText(e.target.value)}
            />
            <Select
              value={folder}
              onValueChange={(v) => {
                setFolder(v);
                setCursors([""]);
                setSelected(null);
              }}
            >
              <SelectTrigger
                className="w-full sm:w-56"
                aria-label="Cache folder"
              >
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="all">All folders</SelectItem>
                {folders.map((f) => (
                  <SelectItem key={f.id} value={f.id}>
                    {f.id}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
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
          <div className="overflow-x-auto rounded-lg border bg-card">
            <table className="w-full text-left text-sm">
              <thead className="text-muted-foreground">
                <tr>
                  <th className="p-3">File</th>
                  <th className="p-3">Cached</th>
                  <th className="hidden p-3 md:table-cell">Last used</th>
                  <th className="p-3">Actions</th>
                </tr>
              </thead>
              <tbody>
                {page.data?.entries.map((e) => (
                  <tr key={e.key} className="border-t">
                    <td className="max-w-64 p-3">
                      {e.source_present ? (
                        <Link
                          className="block truncate text-primary hover:underline"
                          to={`/explorer?item=${encodeURIComponent(e.item)}`}
                        >
                          {e.name || e.item}
                        </Link>
                      ) : (
                        <p className="truncate">{e.name || e.item}</p>
                      )}

                      <p className="break-all text-xs text-muted-foreground">
                        {e.folder} ·{" "}
                        {e.pinned === 1
                          ? "Manual pin"
                          : e.pinned === 2
                            ? "Health protection"
                            : e.pinned !== 0
                              ? "Protected"
                              : "Unpinned"}
                        {e.pending_bytes > 0 && " · pending publication"}
                        {e.dirty !== 0 && " · publication held"}
                        {!e.source_present && " · no current imported source"}
                      </p>
                    </td>
                    <td className="whitespace-nowrap p-3">
                      {bytes(e.cached_bytes)} / {bytes(e.size)}
                    </td>
                    <td className="hidden p-3 md:table-cell">
                      {ago(e.last_used_ms)}
                    </td>
                    <td className="p-3">
                      <div className="flex flex-wrap gap-1">
                        <Button
                          variant="ghost"
                          size="sm"
                          disabled={e.pending_bytes > 0 || e.dirty !== 0}
                          onClick={() => setSelected(e)}
                        >
                          Ranges
                        </Button>
                        <Button
                          variant="ghost"
                          size="sm"
                          disabled={
                            !folders.find((f) => f.id === e.folder)?.writable ||
                            action.isPending ||
                            (e.pinned !== 0 && e.pinned !== 1) ||
                            e.pending_bytes > 0 ||
                            e.dirty !== 0
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
                          variant="ghost"
                          size="sm"
                          disabled={
                            action.isPending ||
                            e.pinned !== 0 ||
                            e.pending_bytes > 0 ||
                            e.dirty !== 0 ||
                            (page.data?.recency_pending ?? 0) > 0 ||
                            (page.data?.recency_dropped ?? 0) > 0 ||
                            !folders.find((f) => f.id === e.folder)
                              ?.eviction_enabled
                          }
                          onClick={() => setEvict(e)}
                        >
                          Evict
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {page.data?.entries.length === 0 && (
              <Empty title="No cached files match">
                Try another folder or search.
              </Empty>
            )}
          </div>
          <div className="flex justify-end gap-2">
            <Button
              variant="outline"
              size="sm"
              disabled={cursors.length === 1 || page.isFetching}
              onClick={() => {
                setCursors(cursors.slice(0, -1));
                setSelected(null);
              }}
            >
              Previous
            </Button>
            <Button
              variant="outline"
              size="sm"
              disabled={!page.data?.next || page.isFetching}
              onClick={() => {
                setCursors([...cursors, page.data!.next!]);
                setSelected(null);
              }}
            >
              Next
            </Button>
          </div>
        </>
      )}
      {selected && (
        <SettingsPanel title="Cached ranges">
          <p className="break-all text-sm">{selected.name || selected.item}</p>
          {ranges.isPending ? (
            <Loading />
          ) : ranges.error ? (
            <Failure error={ranges.error} />
          ) : (
            <p className="break-all font-mono text-xs">
              {ranges.data?.map((r) => `${r.start}–${r.end}`).join(", ") ||
                "No committed ranges"}
            </p>
          )}
          <p className="text-xs text-muted-foreground">
            End offsets are exclusive. This is catalogue coverage; playback
            verifies block hashes.
          </p>
          <OriginalCacheProbe
            key={selected.key}
            entry={selected}
            offset={ranges.data?.[0]?.start}
          />
          <Button variant="outline" size="sm" onClick={() => setSelected(null)}>
            Close ranges
          </Button>
        </SettingsPanel>
      )}
      <AlertDialog
        open={evict !== null}
        onOpenChange={(open) => {
          if (!open) setEvict(null);
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
  entry: Entry;
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
        variant="outline"
        size="sm"
        disabled={offset == null || probe.isPending}
        onClick={() => probe.mutate()}
      >
        Verify cached sample
      </Button>
      <p className="text-xs text-muted-foreground">
        Reads and hash-checks one block (up to 4.2 MB). No provider traffic or
        payload copying.
      </p>
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
