import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { useSettings } from "../settings";
import { bytes } from "../format";
import { Failure } from "../ui";
import { Button } from "@/components/ui/button";

type Entry = Schemas["NativeCacheEntry"];

export function NativeCacheTools({
  showHistory = true,
}: {
  showHistory?: boolean;
}) {
  const settings = useSettings();
  const originalDiagnostics = useMutation({
    mutationFn: () => body(api.GET("/api/v1/native-cache/legacy/diagnostics")),
  });
  const [history, setHistory] = useState(false);
  const [cursors, setCursors] = useState<(number | undefined)[]>([undefined]);
  const before = cursors[cursors.length - 1];
  const diagnostics = useMutation({
    mutationFn: () => body(api.GET("/api/v1/native-cache/diagnostics")),
  });
  const evictions = useQuery({
    queryKey: ["native-cache-evictions", before],
    queryFn: () =>
      body(
        api.GET("/api/v1/native-cache/evictions", {
          params: { query: { before, limit: 50 } },
        }),
      ),
    enabled: history && showHistory,
  });
  return (
    <div className="space-y-3 rounded-lg border p-3">
      <h3 className="text-sm font-medium">Cache inspection</h3>
      <p className="text-sm text-muted-foreground">
        Storage checks inspect folder identity and free space. They do not fetch
        media or adopt a missing volume.
      </p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={diagnostics.isPending || originalDiagnostics.isPending}
          onClick={() => {
            if (settings.data?.settings.native_cache.legacy)
              originalDiagnostics.mutate();
            if ((settings.data?.settings.native_cache.folders.length ?? 0) > 0)
              diagnostics.mutate();
          }}
        >
          {diagnostics.isPending || originalDiagnostics.isPending
            ? "Checking storage…"
            : "Check cache storage"}
        </Button>
        {showHistory && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => setHistory((value) => !value)}
          >
            {history ? "Hide eviction history" : "Show eviction history"}
          </Button>
        )}
      </div>
      {diagnostics.error && <Failure error={diagnostics.error} />}
      {originalDiagnostics.error && (
        <Failure error={originalDiagnostics.error} />
      )}
      {originalDiagnostics.data?.map((f) => (
        <div key={f.id} className="space-y-1 rounded-md border p-3 text-sm">
          <p className="break-all font-mono text-xs">{f.id}</p>
          <p>
            {f.identity_ok === true
              ? "Original folder identity verified"
              : f.identity_ok === false
                ? "Folder identity unavailable or changed"
                : "Storage check uncertain or timed out"}{" "}
            ·{" "}
            {f.free_bytes == null
              ? "Free space unknown"
              : `${bytes(f.free_bytes)} free`}
            {f.read_only === true ? " · Filesystem read only" : ""}
            {f.write_permitted === false
              ? " · Payload writes not permitted"
              : f.write_permitted === true
                ? " · Payload write permission verified"
                : ""}
          </p>
        </div>
      ))}
      {diagnostics.data?.map((folder) => (
        <div
          key={folder.folder_id}
          className="space-y-1 rounded-md border p-3 text-sm"
        >
          <p className="break-all font-mono text-xs">{folder.folder_id}</p>
          <p>
            {folder.identity_ok === true
              ? "Folder identity verified"
              : folder.identity_ok === false
                ? "Folder identity unavailable or changed"
                : "Folder identity uncertain"}{" "}
            · {folder.read_only ? "Read only" : "Writable configuration"}
          </p>
          <p>
            {bytes(folder.committed_bytes)} committed ·{" "}
            {folder.free_bytes == null
              ? "Free space unknown"
              : `${bytes(folder.free_bytes)} free`}{" "}
            · {folder.invalid_blocks} invalid blocks
          </p>
          <p className="text-muted-foreground">
            Quota {bytes(folder.quota_bytes)} · reserve{" "}
            {bytes(folder.minimum_free_bytes)} · checked{" "}
            {new Date(folder.checked_at_ms).toLocaleString()}
          </p>
        </div>
      ))}
      {history && showHistory && (
        <div className="space-y-2">
          <h4 className="text-sm font-medium">Eviction history</h4>
          {evictions.error && <Failure error={evictions.error} />}
          {evictions.isPending ? (
            <p className="text-sm text-muted-foreground">Loading history…</p>
          ) : (
            evictions.data?.length === 0 && (
              <p className="text-sm text-muted-foreground">
                No eviction records on this page.
              </p>
            )
          )}
          {evictions.data?.map((event) => (
            <div
              key={event.cursor}
              className="space-y-1 rounded-md border p-3 text-sm"
            >
              <p>
                {new Date(event.at_ms).toLocaleString()} · {bytes(event.bytes)}
              </p>
              <p className="break-all font-mono text-xs">
                {event.item} · {event.revision}
              </p>
              <p className="break-words">{event.reason}</p>
            </div>
          ))}
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={cursors.length === 1 || evictions.isFetching}
              onClick={() => setCursors((values) => values.slice(0, -1))}
            >
              Newer evictions
            </Button>
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={
                (evictions.data?.length ?? 0) < 50 || evictions.isFetching
              }
              onClick={() => {
                const last = evictions.data?.at(-1);
                if (last) setCursors((values) => [...values, last.cursor]);
              }}
            >
              Older evictions
            </Button>
            <Button
              type="button"
              size="sm"
              variant="ghost"
              disabled={evictions.isFetching}
              onClick={() => {
                if (cursors.length > 1) setCursors([undefined]);
                else void evictions.refetch();
              }}
            >
              Latest evictions
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

// Mount with the selected source/revision as its React key, so a response or cursor
// from a previous selection cannot become the next file's verification progress.
export function NativeCacheProbe({ entry }: { entry: Entry }) {
  const client = useQueryClient();
  const [after, setAfter] = useState(0);
  const [complete, setComplete] = useState(false);
  const probe = useMutation({
    mutationFn: () =>
      body(
        api.POST("/api/v1/native-cache/probe", {
          body: { entry, after_block: after, limit: 16 },
        }),
      ),
    onSuccess: async (report) => {
      setComplete(report.next_block == null);
      if (report.next_block != null) setAfter(report.next_block);
      await Promise.all([
        client.invalidateQueries({ queryKey: ["native-cache-files"] }),
        client.invalidateQueries({ queryKey: ["native-cache-coverage"] }),
        client.invalidateQueries({ queryKey: ["native-cache-status"] }),
      ]);
    },
  });
  return (
    <div className="space-y-2 pt-2">
      <p className="text-sm text-muted-foreground">
        Verify up to 16 stored blocks per click. Confirmed corruption removes
        coverage. Unavailable storage keeps its cursor for a retry. This does
        not download or reconstruct missing bytes.
      </p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          disabled={probe.isPending || complete}
          onClick={() => probe.mutate()}
        >
          {probe.isPending
            ? "Verifying blocks…"
            : after > 0
              ? "Verify next blocks"
              : "Verify cached blocks"}
        </Button>
        {complete && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              setAfter(0);
              setComplete(false);
              probe.reset();
            }}
          >
            Start new verification
          </Button>
        )}
      </div>
      {probe.error && <Failure error={probe.error} />}
      {probe.data && (
        <p className="text-sm" role="status">
          {probe.data.verified} verified · {probe.data.invalidated} invalidated
          · {probe.data.unavailable} unavailable in this batch.{" "}
          {complete
            ? "Verification pass complete."
            : "More blocks or a storage retry remain."}
        </p>
      )}
    </div>
  );
}
