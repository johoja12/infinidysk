import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, body } from "../api/client";
import { bytes } from "../format";
import { Failure, Loading } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { CacheSelect } from "./original-cache-browser";
import { useOriginalCacheOverview } from "./original-cache-overview";
const reasons: Record<string, string> = {
  pressure: "Storage pressure",
  age: "Idle age",
  clear: "Folder clear",
  relocation: "Folder rollover",
  "legacy:manual": "Existing cache: manual",
  "legacy:policy": "Existing cache: policy",
  manual: "Manual removal",
  quota: "Capacity limit",
};
export function CacheEvictions({
  hasOriginal = true,
}: {
  hasOriginal?: boolean;
}) {
  const [source, setSource] = useState(hasOriginal ? "original" : "current"),
    [text, setText] = useState(""),
    [search, setSearch] = useState(""),
    [reason, setReason] = useState(""),
    [folder, setFolder] = useState(""),
    [cursors, setCursors] = useState<(number | undefined)[]>([undefined]);
  const overview = useOriginalCacheOverview(hasOriginal);
  const reset = () => setCursors([undefined]);
  useEffect(() => {
    if (text.trim() === search) return;
    const timer = setTimeout(() => {
      setSearch(text.trim());
      setCursors([undefined]);
    }, 250);
    return () => clearTimeout(timer);
  }, [text, search]);
  const legacy = useQuery({
    queryKey: [
      "legacy-cache-evictions",
      cursors.at(-1),
      search,
      reason,
      folder,
    ],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/native-cache/legacy/evictions", {
          params: {
            query: {
              before: cursors.at(-1),
              search,
              reason: reason || undefined,
              folder: folder || undefined,
            },
          },
          signal,
        }),
      ),
    enabled: source === "original",
  });
  const current = useQuery({
    queryKey: ["native-cache-evictions", cursors.at(-1), search, reason],
    queryFn: ({ signal }) =>
      body(
        api.GET("/api/v1/native-cache/evictions", {
          params: {
            query: {
              before: cursors.at(-1),
              limit: 50,
              search,
              reason: reason || undefined,
            },
          },
          signal,
        }),
      ),
    enabled: source === "current",
  });
  const entries =
    source === "original"
      ? legacy.data?.entries
      : current.data?.map((e) => ({
          cursor: e.cursor,
          key: e.revision,
          folder: "Not recorded",
          item: e.item,
          name: null,
          size: 0,
          allocated_bytes: e.bytes,
          cached_bytes: 0,
          reason: e.reason,
          at_ms: e.at_ms,
        }));
  const next =
    source === "original"
      ? legacy.data?.next
      : current.data?.length === 50
        ? current.data.at(-1)?.cursor
        : undefined;
  const error = source === "original" ? legacy.error : current.error,
    pending = source === "original" ? legacy.isPending : current.isPending;
  return (
    <section className="space-y-4" aria-label="Eviction history">
      <div>
        <h2 className="text-lg font-medium">Eviction history</h2>
        <p className="mt-1 text-xs text-muted-foreground">
          Confirmed removals only. Viewing history does not enable eviction or
          remove anything.
        </p>
      </div>
      <div className="flex flex-wrap gap-2 rounded-lg border bg-card p-3">
        <CacheSelect
          label="Eviction history source"
          value={source}
          options={[
            ...(hasOriginal
              ? ([["original", "Original history (v1)"]] as [string, string][])
              : []),
            ["current", "Service history (v2)"],
          ]}
          onChange={(v) => {
            setSource(v);
            setText("");
            setSearch("");
            setReason("");
            setFolder("");
            reset();
          }}
        />
        <Input
          type="search"
          aria-label="Search evictions"
          className="min-w-40 flex-1"
          placeholder={
            source === "original"
              ? "Search file name or ID…"
              : "Search file ID or revision…"
          }
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        {source === "original" && (
          <CacheSelect
            label="Filter eviction folder"
            value={folder}
            options={[
              ["", "All folders"],
              ...(overview.data?.folders ?? []).map(
                (f) => [f.id, f.id] as [string, string],
              ),
            ]}
            onChange={(v) => {
              setFolder(v);
              reset();
            }}
          />
        )}
        <CacheSelect
          label="Filter eviction reason"
          value={reason}
          options={[
            ["", "All reasons"],
            ...Object.entries(reasons).filter(
              ([v]) =>
                source !== "original" ||
                ["pressure", "age", "clear", "relocation"].includes(v),
            ),
          ]}
          onChange={(v) => {
            setReason(v);
            reset();
          }}
        />
        <Button
          size="sm"
          variant="ghost"
          onClick={() => {
            setText("");
            setSearch("");
            setFolder("");
            setReason("");
            reset();
          }}
        >
          Clear history filters
        </Button>
      </div>
      <p className="text-xs text-muted-foreground">
        {source === "original"
          ? `${legacy.data?.total?.toLocaleString() ?? "…"} matching original records. Historical names and folders are preserved.`
          : "Events recorded by the current service. Earlier names and folder identities are not stored in this event log; use Original history for v1 records."}
      </p>
      {error && <Failure error={error} />}{" "}
      {pending ? (
        <Loading />
      ) : source === "original" && legacy.data?.available === false ? (
        <p className="text-sm text-warning-text">
          This catalogue does not contain original eviction history.
        </p>
      ) : (
        <div className="overflow-hidden rounded-lg border bg-card">
          {entries?.map((e) => (
            <div
              key={e.cursor}
              className="grid gap-3 border-b p-4 last:border-0 md:grid-cols-[minmax(0,2fr)_8rem_8rem_10rem]"
            >
              <div className="min-w-0">
                <p
                  className="truncate text-sm font-medium"
                  title={e.name || e.item}
                >
                  {e.name || e.item.slice(0, 8)}
                </p>
                <p className="mt-1 text-xs text-muted-foreground">{e.folder}</p>
              </div>
              <p className="text-xs tabular-nums">
                {bytes(e.allocated_bytes)} released
                {source === "original" && (
                  <span className="mt-1 block text-muted-foreground">
                    {bytes(e.cached_bytes)} covered / {bytes(e.size)}
                  </span>
                )}
              </p>
              <p className="text-xs text-muted-foreground">
                {reasons[e.reason] ?? e.reason}
              </p>
              <div className="text-xs text-muted-foreground">
                <p>{new Date(e.at_ms).toLocaleString()}</p>
                <details className="mt-2">
                  <summary className="cursor-pointer">Record identity</summary>
                  <p className="mt-1 break-all">{e.item}</p>
                  <p className="break-all">{e.key}</p>
                </details>
              </div>
            </div>
          ))}
          {!entries?.length && (
            <p className="p-8 text-center text-sm text-muted-foreground">
              No eviction records match this view.
            </p>
          )}
        </div>
      )}
      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
        <p>Page {cursors.length} · up to 50 records per page</p>
        <div className="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={cursors.length === 1 || pending}
            onClick={() => setCursors((c) => c.slice(0, -1))}
          >
            Newer evictions
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={next == null || pending}
            onClick={() => setCursors((c) => [...c, next!])}
          >
            Older evictions
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              if (cursors.length > 1) reset();
              else if (source === "original") void legacy.refetch();
              else void current.refetch();
            }}
          >
            Latest evictions
          </Button>
        </div>
      </div>
    </section>
  );
}
