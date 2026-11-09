import { useState } from "react";
import { type Schemas } from "../api/client";
import { Field, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
type Source = Schemas["PlexPrefetchSource"];
export function PlexPrefetchSources({value,servers,disabled,apply}:{value:Source[];servers:Schemas["PlexServer"][];disabled:boolean;apply:(sources:Source[])=>Promise<unknown>}) {
  const [draft,setDraft]=useState<Source[]|null>(null);
  const rows=draft??value;
  const patch=(index:number,change:Partial<Source>)=>setDraft(rows.map((row,i)=>i===index?{...row,...change}:row));
  const invalid=rows.some(row=>!row.server || !row.viewer.trim() || !row.path.trim() || !Number.isInteger(row.limit) || row.limit<1 || row.limit>1000);
  return <section className="space-y-3 border-t pt-4">
    <h3 className="text-sm font-medium">Hub, collection and library sources</h3>
    <p className="text-xs text-muted-foreground">Explicit candidate resources use a verified viewer credential. Paths stay on the selected Plex server and must start with /hubs/ or /library/. Never include tokens. Limits select an ordered prefix; a failed or incomplete refresh retains the previous snapshot. Shows expand to the next unwatched episodes.</p>
    {rows.map((row,index)=><div key={index} className="space-y-3 rounded-lg border p-3">
      <Toggle id={`source-enabled-${index}`} label={`Enable source ${index+1}`} checked={row.enabled} disabled={disabled} onChange={enabled=>patch(index,{enabled})}/>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field id={`source-server-${index}`} label="Plex server"><Select value={row.server} disabled={disabled} onValueChange={server=>patch(index,{server})}><SelectTrigger id={`source-server-${index}`}><SelectValue placeholder="Select a configured server"/></SelectTrigger><SelectContent>{servers.map(server=><SelectItem key={server.id} value={server.id}>{server.name || server.id}</SelectItem>)}</SelectContent></Select></Field>
        <Field id={`source-viewer-${index}`} label="Verified viewer ID" hint="An exact account/profile ID. Names do not authorize access."><Input id={`source-viewer-${index}`} disabled={disabled} value={row.viewer} onChange={e=>patch(index,{viewer:e.target.value})}/></Field>
        <Field id={`source-kind-${index}`} label="Source type"><Select value={row.kind} disabled={disabled} onValueChange={kind=>patch(index,{kind:kind as Source["kind"]})}><SelectTrigger id={`source-kind-${index}`}><SelectValue/></SelectTrigger><SelectContent><SelectItem value="hub">Hub</SelectItem><SelectItem value="collection">Collection</SelectItem><SelectItem value="library">Library</SelectItem></SelectContent></Select></Field>
        <Field id={`source-limit-${index}`} label="Candidate limit"><Input id={`source-limit-${index}`} disabled={disabled} type="number" min={1} max={1000} value={row.limit} onChange={e=>patch(index,{limit:Number(e.target.value)})}/></Field>
      </div>
      <Field id={`source-path-${index}`} label="Server-relative catalogue path"><Input id={`source-path-${index}`} disabled={disabled} value={row.path} placeholder={row.kind==="hub"?"/hubs/…":"/library/…"} onChange={e=>patch(index,{path:e.target.value})}/></Field>
      <Field id={`source-exclusions-${index}`} label="Excluded show IDs" hint="Comma-separated exact Plex IDs for this source."><Input id={`source-exclusions-${index}`} disabled={disabled} value={row.excluded_shows.join(", ")} onChange={e=>patch(index,{excluded_shows:e.target.value.split(",").map(s=>s.trim()).filter(Boolean)})}/></Field>
      <Button type="button" size="sm" variant="ghost" disabled={disabled} onClick={()=>setDraft(rows.filter((_,i)=>i!==index))}>Remove source {index+1}</Button>
    </div>)}
    <div className="flex flex-wrap gap-2">
      <Button type="button" variant="outline" disabled={disabled || rows.length>=128 || servers.length===0} onClick={()=>setDraft([...rows,{enabled:true,server:servers[0]?.id??"",viewer:"",kind:"hub",path:"",limit:50,excluded_shows:[]}])}>Add source</Button>
      <Button type="button" variant="outline" disabled={disabled || draft===null || invalid} onClick={()=>{void apply(rows).then(()=>setDraft(null)).catch(()=>{});}}>Apply sources</Button>
      {draft!==null && <Button type="button" variant="ghost" disabled={disabled} onClick={()=>setDraft(null)}>Discard source changes</Button>}
    </div>
    {servers.length===0 && <p className="text-xs text-muted-foreground">Connect a Plex server before adding sources.</p>}
  </section>;
}
