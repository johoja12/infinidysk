import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { Failure, Panel } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

export function ContentSweep() {
  const client = useQueryClient();
  const [category, setCategory] = useState("");
  const [limit, setLimit] = useState("");
  const query = useQuery({queryKey: ["content-sweep"], queryFn: () => body(api.GET("/api/v1/health/content-sweep")), refetchInterval: 5000});
  const action = useMutation({
    mutationFn: (control: Schemas["ContentSweepOperation"]) => body(api.POST("/api/v1/health/content-sweep", {body: {...control, revision: query.data!.revision}})),
    onSuccess: async () => {await client.invalidateQueries({queryKey: ["content-sweep"]});},
    onError: async error => {toast.error(error.message); await client.invalidateQueries({queryKey: ["content-sweep"]});},
  });
  const state = query.data?.state;
  const valid = !limit || (/^\d+$/.test(limit) && Number(limit) > 0 && Number(limit) <= 10000000);
  return <Panel className="mb-6 space-y-3 p-4">
    <h2 className="font-medium">Imported content sweep</h2>
    <p className="text-sm text-muted-foreground">Samples decoded articles from each imported media file. Progress survives restarts. Confirmed damage enters normal health confirmation and repair; unavailable providers remain unverified. Foreground playback and imports pause the sampling lane.</p>
    {query.isError && <Failure error={query.error} />}
    {state && <>
      <p className="text-sm">{state.status} · {state.checked} / {query.data?.total ?? 0} processed · {state.healthy} sampled healthy · {state.damaged} damaged · {state.unverifiable} unverified · {state.skipped} skipped</p>
      <div className="flex flex-wrap items-end gap-2">
        <label className="space-y-1 text-sm">Category (blank for all)<Input aria-label="Sweep category" value={category} maxLength={128} onChange={e => setCategory(e.target.value)} disabled={state.status === "running" || action.isPending} /></label>
        <label className="space-y-1 text-sm">Files per run (blank for all)<Input aria-label="Sweep limit" inputMode="numeric" value={limit} onChange={e => setLimit(e.target.value)} disabled={state.status === "running" || action.isPending} /></label>
        <Button disabled={action.isPending || state.status === "running" || !valid || /[/\\\x00-\x1f]/.test(category) || [".", ".."].includes(category)} onClick={() => action.mutate({operation: "start", category: category || null, limit: limit ? Number(limit) : null})}>Start new sweep</Button>
        {state.status === "running" && <Button variant="outline" disabled={action.isPending} onClick={() => action.mutate({operation: "pause"})}>Pause sweep</Button>}
        {state.status === "paused" && <Button variant="outline" disabled={action.isPending} onClick={() => action.mutate({operation: "resume"})}>Resume sweep</Button>}
      </div>
      <p className="text-xs text-muted-foreground">Starting a new sweep replaces its candidate snapshot and counters. Resume continues the saved snapshot with the same category and per-run limit. A healthy sample does not verify every byte.</p>
      {state.recent.length > 0 && <details><summary className="cursor-pointer text-sm">Recent findings ({state.recent.length})</summary><ul className="mt-2 space-y-1 text-xs">{state.recent.slice().reverse().map((finding, i) => <li className="break-all" key={`${finding.item}-${i}`}>{finding.outcome} · source {finding.item}</li>)}</ul></details>}
    </>}
  </Panel>;
}
