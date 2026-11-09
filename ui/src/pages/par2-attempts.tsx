import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router";
import { api, body } from "../api/client";
import { ago, bytes, stamp } from "../format";
import { Failure, Loading, Panel } from "../ui";

export function Par2Attempts() {
  const query = useQuery({queryKey: ["par2-attempts"], queryFn: () => body(api.GET("/api/v1/health/par2")), refetchInterval: 10000});
  return <section className="mb-6 space-y-3">
    <h2 className="font-medium">Local PAR2 repair</h2>
    <p className="text-sm text-muted-foreground">Up to 128 recent attempts. A repaired result still needs a fresh health check. Provider outages retain urgency. Existing repaired data stays active when new attempts are disabled.</p>
    {query.isPending ? <Loading /> : query.isError ? <Failure error={query.error} /> : query.data.length === 0 ? <p className="text-sm text-muted-foreground">No local repair attempts recorded.</p> : query.data.map(row => <Panel key={row.id} className="space-y-2 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2"><span className="text-sm font-medium">{row.state}{row.urgent ? " · Playback or warming priority" : ""}</span><span className="text-xs text-muted-foreground">Updated {ago(row.updated_at)}</span></div>
      <Link to={`/explorer?${new URLSearchParams({item: row.item})}`} className="block break-all font-mono text-xs hover:underline">Source {row.item}</Link>
      {row.reason && <p className="text-sm">{row.reason}</p>}
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"><span>{row.attempts} attempts</span><span>{row.patch_articles} repaired articles · {bytes(row.patch_bytes)}</span>{["queued", "deferred", "publishing"].includes(row.state) && <span>Next attempt {stamp(row.next_attempt)}</span>}</div>
    </Panel>)}
  </section>;
}
