import { useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "../api/client";
import { bytes } from "../format";
import { Button } from "@/components/ui/button";
import { Failure } from "../ui";

type Candidate = Schemas["NativeCacheRecoveryCandidate"];
type Page = Schemas["NativeCacheRecoveryPage"];
type Progress = { offset: number; processed: number; complete: boolean };
export function NativeCacheRecovery({ folders }: { folders: Schemas["NativeCacheFolder"][] }) {
  const client = useQueryClient();
  const status = useQuery({ queryKey: ["native-cache-status"], queryFn: () => body(api.GET("/api/v1/native-cache")), refetchInterval: 5000 });
  const [folder, setFolder] = useState("");
  const [page, setPage] = useState<Page | null>(null);
  const [progress, setProgress] = useState<Record<string, Progress>>({});
  const [running, setRunning] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [failure, setFailure] = useState<Error | null>(null);
  const failed = (error: unknown) => error instanceof Error ? error : new Error("Cache recovery request failed");
  const active = useRef(false);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { active.current = false; mounted.current = false; }; }, []);
  const inspect = async (after?: string) => {
    if (!folder) return;
    setLoading(true); setFailure(null);
    try { setPage(await body(api.GET("/api/v1/native-cache/recovery", { params: { query: { folder_id: folder, after } } }))); }
    catch (error) { setFailure(failed(error)); }
    finally { setLoading(false); }
  };
  const recover = async (source: Candidate) => {
    if (active.current) return;
    active.current = true; setRunning(source.key); setFailure(null);
    let current = progress[source.key] ?? { offset: 0, processed: 0, complete: false };
    try {
      while (active.current && !current.complete) {
        const result = await body(api.POST("/api/v1/native-cache/recovery", { body: { folder_id: folder, source, offset: current.offset } }));
        current = { offset: result.next_offset ?? 0, processed: current.processed + result.verified_bytes, complete: result.next_offset == null };
        if (mounted.current) setProgress(values => ({ ...values, [source.key]: current }));
      }
      await client.invalidateQueries({ queryKey: ["native-cache-files"] });
      await client.invalidateQueries({ queryKey: ["native-cache-status"] });
    } catch (error) { if (mounted.current) setFailure(failed(error)); }
    finally { active.current = false; if (mounted.current) setRunning(null); }
  };
  return <section className="space-y-3 rounded-lg border p-3">
    <h3 className="text-sm font-medium">Recover cache coverage</h3>
    <p className="text-sm text-muted-foreground">Save settings and restart in recovery mode first. Payload writes, warming and eviction stay off. Incomplete recovery coverage stays uncertain for automatic repair. Recovery verifies recorded checksums against the current source and repair revision. It cannot infer missing records from file size. New copies start pinned until you review retention. This does not activate 1.x cache folders.</p>
    {!status.data?.recovery_mode && <p className="text-sm text-muted-foreground">The running cache is not in recovery mode.</p>}
    <div className="flex flex-wrap gap-2">{folders.filter(folder => folder.enabled).map(value => <Button key={value.id} className="h-auto max-w-full whitespace-normal break-all text-left" type="button" size="sm" variant={folder === value.id ? "secondary" : "outline"} disabled={!status.data?.recovery_mode || running !== null || loading} onClick={() => { setFolder(value.id); setPage(null); setProgress({}); setFailure(null); }}>{value.path}</Button>)}</div>
    <div className="flex flex-wrap gap-2">
      <Button type="button" size="sm" variant="outline" disabled={!folder || !status.data?.recovery_mode || loading || running !== null} onClick={() => { void inspect(); }}>Inspect recovery records</Button>
      {page?.after && <Button type="button" size="sm" variant="outline" disabled={loading || running !== null} onClick={() => { void inspect(page.after ?? undefined); }}>Next sources</Button>}
      {running !== null && <Button type="button" size="sm" variant="outline" onClick={() => { active.current = false; }}>Stop after this page</Button>}
    </div>
    {failure != null && <Failure error={failure} />}
    {page && <p className="text-xs text-muted-foreground">{page.entries.length} sources on this page · {page.rejected} invalid records skipped. Recovery can be repeated after a restart; every page verifies its bytes again.</p>}
    {page?.entries.map(source => <div key={source.key} className="space-y-2 rounded-md border p-3">
      <p className="break-all font-mono text-xs">{source.item} · {source.revision}</p>
      <p className="text-xs text-muted-foreground">Source size {bytes(source.size_bytes)} · verified bytes processed {bytes(progress[source.key]?.processed ?? 0)}{progress[source.key]?.complete ? " · pass complete" : ""}</p>
      <Button type="button" size="sm" variant="outline" disabled={running !== null || progress[source.key]?.complete} onClick={() => { void recover(source); }}>{running === source.key ? "Verifying…" : (progress[source.key]?.offset ?? 0) > 0 ? "Resume verification" : "Recover verified coverage"}</Button>
    </div>)}
  </section>;
}
