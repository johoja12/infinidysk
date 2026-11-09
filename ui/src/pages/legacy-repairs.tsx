import { useQuery } from "@tanstack/react-query";
import { api, body } from "../api/client";
import { ago } from "../format";
import { Failure, Loading, Panel } from "../ui";

export function LegacyRepairs() {
  const query = useQuery({ queryKey: ["legacy-repairs"], queryFn: () => body(api.GET("/api/v1/health/legacy-repairs")), refetchInterval: 15000 });
  return <section className="mb-6 space-y-3">
    <h2 className="font-medium">Repairs carried over from 1.x</h2>
    <p className="text-sm text-muted-foreground">Up to 128 records. Uncertain requests stay held. Retirement needs a fresh replacement check, the original source generation, no readers and safe storage. No Arr action is replayed.</p>
    {query.isPending ? <Loading /> : query.isError ? <Failure error={query.error} /> : query.data.length === 0 ? <p className="text-sm text-muted-foreground">No legacy repair records.</p> : query.data.map(row => <Panel key={row.evidence_id} className="space-y-2 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="text-sm font-medium">{row.retired_at !== null ? "Old source retired" : row.resolved_at === null ? "Awaiting replacement proof" : row.source_present ? "Replacement observed · Retirement held" : "Replacement observed · Source absent"}</span>
        {row.observed_at > 0 && <span className="text-xs text-muted-foreground">Checked {ago(row.observed_at)}</span>}
      </div>
      <p className="break-all font-mono text-xs">{row.item_id ? `Original source ${row.item_id}` : "Unbound record · Holds replacement until reconciled"}</p>
      <p className="text-xs text-muted-foreground">{row.kind} · Evidence {row.evidence_id.slice(0, 16)}</p>
    </Panel>)}
  </section>;
}
