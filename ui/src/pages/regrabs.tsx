import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { ago } from "../format";
import { Failure, Loading, Panel, TextConfirmDialog } from "../ui";
import { Button } from "@/components/ui/button";

type Journal = Schemas["RegrabStatus"];
type Cursor = Schemas["RegrabCursor"];

export function Regrabs() {
  const client = useQueryClient();
  const [cursors, setCursors] = useState<(Cursor | null)[]>([null]);
  const cursor = cursors.at(-1);
  const [abandon, setAbandon] = useState<Journal | null>(null);
  const query = useQuery({
    queryKey: ["regrabs", cursor],
    queryFn: () => body(api.GET("/api/v1/health/regrabs", {params: {query: cursor ?? {}}})),
    refetchInterval: 15000,
  });
  const control = useMutation({
    mutationFn: ({row, operation}: {row: Journal; operation: Schemas["RegrabOperation"]}) => body(api.POST("/api/v1/health/regrabs/{id}", {
      params: {path: {id: row.item}}, body: {revision: row.revision, operation},
    })),
    onSuccess: async () => {
      setAbandon(null);
      await Promise.all([client.invalidateQueries({queryKey: ["regrabs"]}), client.invalidateQueries({queryKey: ["file"]})]);
    },
    onError: async error => {toast.error(error.message); await client.invalidateQueries({queryKey: ["regrabs"]});},
  });
  return <section className="mb-6 space-y-3">
    <h2 className="font-medium">Replacement recovery</h2>
    <p className="text-sm text-muted-foreground">Saved requests remain here after their source is removed. Check now only observes Arr. Resume keeps the last confirmed checkpoint and reconciles uncertain responses before further actions.</p>
    {query.isPending ? <Loading /> : query.isError ? <Failure error={query.error} /> : <>
      {query.data.entries.length === 0 && <p className="text-sm text-muted-foreground">No replacement records on this page.</p>}
      {query.data.entries.map(row => <Panel key={row.item} className="space-y-2 p-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-sm font-medium">{row.stage.replaceAll("_", " ")}</span>
          <span className="text-xs text-muted-foreground">Updated {ago(row.updated_at)}</span>
        </div>
        <p className="break-all font-mono text-xs">Source {row.item}</p>
        {row.observation && <p className="text-sm">{row.observation}</p>}
        {row.command_id != null && <p className="text-xs text-muted-foreground">Arr command {row.command_id}</p>}
        {!["unreadable", "unsupported", "replaced"].includes(row.stage) && <div className="flex flex-wrap gap-2">
          <Button type="button" size="sm" variant="outline" disabled={control.isPending} onClick={() => control.mutate({row, operation: "observe"})}>Check now</Button>
          {row.abandoned ? <Button type="button" size="sm" variant="outline" disabled={control.isPending} onClick={() => control.mutate({row, operation: "resume"})}>Resume recovery</Button> :
            <Button type="button" size="sm" variant="ghost" disabled={control.isPending} onClick={() => setAbandon(row)}>Abandon recovery</Button>}
        </div>}
      </Panel>)}
      <div className="flex flex-wrap gap-2">
        <Button type="button" size="sm" variant="outline" disabled={cursors.length === 1 || query.isFetching} onClick={() => setCursors(values => values.slice(0, -1))}>Newer requests</Button>
        <Button type="button" size="sm" variant="outline" disabled={!query.data.next || query.isFetching} onClick={() => {if (query.data.next) setCursors(values => [...values, query.data.next!]);}}>Older requests</Button>
        <Button type="button" size="sm" variant="ghost" disabled={query.isFetching} onClick={() => {if (cursors.length > 1) setCursors([null]); else void query.refetch();}}>Latest requests</Button>
      </div>
    </>}
    <TextConfirmDialog open={abandon !== null} onOpenChange={open => {if (!open) setAbandon(null);}}
      title="Abandon replacement recovery?"
      message="Stops future automatic recovery for this request. Arr actions already accepted or in flight are not undone. The journal is retained, and recovery can resume while the original source identity remains valid."
      expected="ABANDON" confirm="Abandon recovery" onConfirm={() => {if (abandon) control.mutate({row: abandon, operation: "abandon"});}} />
  </section>;
}
