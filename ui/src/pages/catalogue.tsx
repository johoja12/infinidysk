import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link } from "react-router";
import { RefreshCw } from "lucide-react";
import { PlexClassification } from "./library-plex";
import { toast } from "sonner";
import { api, body } from "../api/client";
import { ago, bytes } from "../format";
import { Empty, Failure, Loading, PageHeader, Panel, useTitle } from "../ui";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";

const PAGE = 100;

export function Catalogue() {
  useTitle("Library");
  const client = useQueryClient();
  const [selected, setSelected] = useState("");
  const [group, setGroup] = useState<string>();
  const [after, setAfter] = useState("");
  const [previous, setPrevious] = useState<string[]>([]);
  const roots = useQuery({ queryKey: ["library-catalogue"], queryFn: () => body(api.GET("/api/v1/library/catalogue")), refetchInterval: 5000 });
  const root = roots.data?.find((r) => r.root === selected) ?? roots.data?.[0];
  const groups = useQuery({
    queryKey: ["library-catalogue-groups", root?.root, after],
    queryFn: () => body(api.GET("/api/v1/library/catalogue/groups", { params: { query: { root: root!.root, after, limit: PAGE } } })),
    enabled: !!root && group === undefined,
    refetchInterval: root?.stale ? 5000 : 30000,
  });
  const files = useQuery({
    queryKey: ["library-catalogue-files", root?.root, group, after],
    queryFn: () => body(api.GET("/api/v1/library/catalogue/files", { params: { query: { root: root!.root, group, after, limit: PAGE } } })),
    enabled: !!root && group !== undefined,
    refetchInterval: root?.stale ? 5000 : 30000,
  });
  const refresh = useMutation({
    mutationFn: () => body(api.POST("/api/v1/library/catalogue", { body: { root: root?.root ?? null } })),
    onSuccess: () => { void client.invalidateQueries({ queryKey: ["library-catalogue"] }); toast.success("Refresh queued. Scans wait for playback and imports to be idle."); },
  });
  const reset = () => { setAfter(""); setPrevious([]); };
  const rows = group === undefined ? groups.data : files.data;
  const error = group === undefined ? groups.error : files.error;
  const loading = group === undefined ? groups.isPending : files.isPending;
  const next = () => {
    const cursor = group === undefined ? groups.data?.at(-1)?.group : files.data?.at(-1)?.path;
    if (cursor) { setPrevious([...previous, after]); setAfter(cursor); }
  };
  return <>
    <PageHeader title="Library" description="Video files linked from your configured library folders." />
    <div className="mb-4"><PlexClassification /></div>
    {roots.isPending ? <Loading /> : roots.isError ? <Failure error={roots.error} /> : !root ?
      <Empty title="No library folders configured"><Link to="/settings/library" className="text-primary underline">Configure library folders</Link> to build the index.</Empty> : <>
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <Select value={root.root} onValueChange={(v) => { setSelected(v); setGroup(undefined); reset(); }}>
            <SelectTrigger className="w-full min-w-0 sm:w-96" aria-label="Library folder"><SelectValue /></SelectTrigger>
            <SelectContent>{roots.data?.map((r) => <SelectItem key={r.root} value={r.root}>{r.root}{r.read_only ? " · read only" : ""}</SelectItem>)}</SelectContent>
          </Select>
          <Button variant="outline" size="sm" onClick={() => refresh.mutate()} disabled={refresh.isPending}><RefreshCw />Refresh index</Button>
          {group !== undefined && <Button variant="ghost" size="sm" onClick={() => { setGroup(undefined); reset(); }}>All groups</Button>}
        </div>
        {refresh.isError && <Failure error={refresh.error} className="mb-3" />}
        <p className="text-muted-foreground mb-4 text-sm break-words" role="status">
          {root.error ?? (root.stale ? "Index needs a complete scan. Existing results may be stale." : "Index is current.")}
          {root.read_only && <> Read-only discovery: replacement authority requires a managed library mapping.</>}
          {root.scanned_at != null && <> Last complete scan {ago(root.scanned_at)}.</>}
          {root.scan_entries != null && <> Scan progress: {root.scan_entries.toLocaleString()} entries checked · {(root.scan_links ?? 0).toLocaleString()} links found. The last complete index stays visible.{root.scan_updated_at != null && <> Last progress {ago(root.scan_updated_at)}.</>}</>}
        </p>
        {group !== undefined && <h2 className="mb-3 break-words font-medium">{group}</h2>}
        {loading ? <Loading /> : error ? <Failure error={error} /> : rows?.length === 0 ?
          <Empty title={root.stale ? "No saved results yet" : "No linked videos found"}>Refresh after adding files or configuring folders.</Empty> :
          <Panel><Table><TableHeader><TableRow><TableHead>{group === undefined ? "Group" : "File"}</TableHead><TableHead>{group === undefined ? "Videos" : "Pointer"}</TableHead><TableHead className="hidden sm:table-cell">{group === undefined ? "Size" : "Source"}</TableHead></TableRow></TableHeader>
            <TableBody>{group === undefined ? groups.data?.map((g) => <TableRow key={g.group}>
              <TableCell className="max-w-64"><Button variant="link" className="h-auto max-w-full justify-start truncate px-0" onClick={() => { setGroup(g.group); reset(); }}>{g.group}</Button>{g.unavailable > 0 && <p className="text-warning-text text-xs">{g.unavailable} source unavailable</p>}</TableCell>
              <TableCell>{g.files}</TableCell><TableCell className="hidden sm:table-cell">{bytes(g.bytes)}</TableCell>
            </TableRow>) : files.data?.map((f) => <TableRow key={f.path}>
              <TableCell className="max-w-48"><Link className="text-primary block truncate hover:underline" title={f.relative_path} to={`/explorer?item=${encodeURIComponent(f.item)}&search=${encodeURIComponent(f.relative_path.split("/").at(-1) ?? "")}`}>{f.relative_path}</Link></TableCell>
              <TableCell>{f.link_kind === "strm" ? "STRM" : "Symlink"}</TableCell><TableCell className="hidden max-w-40 truncate font-mono text-xs sm:table-cell" title={f.item}>{f.item}</TableCell>
            </TableRow>)}</TableBody></Table></Panel>}
        <div className="mt-3 flex justify-end gap-2">
          <Button variant="outline" size="sm" disabled={!previous.length} onClick={() => { setAfter(previous.at(-1) ?? ""); setPrevious(previous.slice(0, -1)); }}>Previous</Button>
          <Button variant="outline" size="sm" disabled={loading || rows?.length !== PAGE} onClick={next}>Next</Button>
        </div>
      </>}
  </>;
}
