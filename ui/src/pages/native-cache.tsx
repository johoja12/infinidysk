import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { bytes } from "../format";
import { Failure, Field, Locked, SettingsPanel, Toggle } from "../ui";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle } from "@/components/ui/alert-dialog";

import { NativeCacheRecovery } from "./native-cache-recovery";
import { NativeCacheProbe, NativeCacheTools } from "./native-cache-tools";

type Settings = Schemas["NativeCacheSettings"];
type Entry = Schemas["NativeCacheEntry"];

export function NativeCachePanel({value,locked,onChange}:{value:Settings;locked:(path:string)=>boolean;onChange:(value:Settings)=>void}) {
  const patch=(change:Partial<Settings>)=>onChange({...value,...change});
  const folder=(index:number,change:Partial<Schemas["NativeCacheFolder"]>)=>patch({folders:value.folders.map((f,i)=>i===index?{...f,...change}:f)});
  return <>
    <SettingsPanel title="Final-media cache">
      <p className="text-sm text-muted-foreground">Keeps verified media chunks for playback over FUSE, WebDAV and STRM. Storage changes take effect after a restart. Use a separate, empty folder for a new cache.</p>
      <Toggle id="native-cache-enabled" label={<>Enable final-media cache <Locked locked={locked("native_cache.enabled")} /></>} checked={value.enabled} disabled={locked("native_cache.enabled")} onChange={enabled=>patch({enabled})}/>
      <Toggle id="native-recovery-mode" label={<>Recovery mode <Locked locked={locked("native_cache.recovery_mode")} /></>} hint="Requires a restart. Reconstructs coverage only from matching Rust recovery records and verified bytes. Payload writes and eviction stay off until you turn this off and restart. Existing older chunks without records cannot be reconstructed." checked={value.recovery_mode} disabled={locked("native_cache.recovery_mode")} onChange={recovery_mode=>patch({recovery_mode})}/>
      <Field id="native-catalogue" label="Local catalogue directory" hint="Keep this on local storage, separate from media cache volumes.">
        <Input id="native-catalogue" value={value.metadata_path} disabled={locked("native_cache.metadata_path")} onChange={e=>patch({metadata_path:e.target.value})}/>
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        {([['buffer_mb','Buffer budget (MB)'],['min_file_mb','Minimum file size (MB)'],['chunk_mb','Chunk target (MB)'],['io_timeout_ms','Storage wait limit (ms)']] as const).map(([key,label])=><Field key={key} id={`native-${key}`} label={label} hint={key==='chunk_mb'?'Rounded up to a verified block boundary. Existing entries retain their layout.':undefined}><Input id={`native-${key}`} type="number" min={key==='min_file_mb'?0:key==='buffer_mb'?9:key==='io_timeout_ms'?100:4} value={value[key]} disabled={locked(`native_cache.${key}`)} onChange={e=>patch({[key]:Number(e.target.value)})}/></Field>)}
      </div>
      <p className="text-sm text-muted-foreground">Disable the disk segment cache before enabling this cache. If storage is unavailable or slow, playback uses the source.</p>
      <div className="space-y-4">
        {value.folders.map((f,i)=><div key={f.id} className="space-y-3 rounded-lg border p-4">
          <div className="flex items-center justify-between"><span className="text-sm font-medium">Cache folder {i+1}</span><Button type="button" variant="ghost" size="sm" disabled={locked("native_cache.folders")} onClick={()=>patch({folders:value.folders.filter((_,j)=>j!==i)})}>Remove</Button></div>
          <Field id={`native-folder-${f.id}-path`} label="Path"><Input id={`native-folder-${f.id}-path`} value={f.path} disabled={locked("native_cache.folders")} onChange={e=>folder(i,{path:e.target.value})}/></Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Quota (GB)"><Input type="number" min={1} value={f.quota_bytes/1e9} disabled={locked("native_cache.folders")} onChange={e=>folder(i,{quota_bytes:Number(e.target.value)*1e9})}/></Field>
            <Field label="Keep free (GB)"><Input type="number" min={0} value={f.min_free_bytes/1e9} disabled={locked("native_cache.folders")} onChange={e=>folder(i,{min_free_bytes:Number(e.target.value)*1e9})}/></Field>
            <Field label="Placement priority"><Input type="number" value={f.priority} disabled={locked("native_cache.folders")} onChange={e=>folder(i,{priority:Number(e.target.value)})}/></Field>
            <Field label="Maximum age (hours)" hint="Zero keeps files until space is needed."><Input type="number" min={0} value={f.max_age_hours} disabled={locked("native_cache.folders")} onChange={e=>folder(i,{max_age_hours:Number(e.target.value)})}/></Field>
          </div>
          <Toggle id={`native-folder-${f.id}-enabled`} label="Enabled" checked={f.enabled} disabled={locked("native_cache.folders")} onChange={enabled=>folder(i,{enabled})}/>
          <Toggle id={`native-folder-${f.id}-readonly`} label="Read only" checked={f.read_only} disabled={locked("native_cache.folders")} onChange={read_only=>folder(i,{read_only})}/>
        </div>)}
        <Button type="button" variant="outline" disabled={locked("native_cache.folders")} onClick={()=>patch({folders:[...value.folders,{id:crypto.randomUUID(),path:"",enabled:true,read_only:false,priority:0,quota_bytes:100e9,min_free_bytes:1e9,max_age_hours:0,high_percent:90,low_percent:80}]})}>Add cache folder</Button>
      </div>
    </SettingsPanel>
    <NativeCacheBrowser folders={value.folders} />
    <LegacyCacheImport />
  </>;
}
function NativeCacheBrowser({folders}:{folders:Schemas["NativeCacheFolder"][]}) {
  const queryClient=useQueryClient();const [offset,setOffset]=useState(0);const [selected,setSelected]=useState<Entry|null>(null);const [evict,setEvict]=useState<Entry|null>(null);
  const status=useQuery({queryKey:['native-cache-status'],queryFn:()=>body(api.GET('/api/v1/native-cache')),refetchInterval:5000});
  const entries=useQuery({queryKey:['native-cache-files',offset],queryFn:()=>body(api.GET('/api/v1/native-cache/files',{params:{query:{offset,limit:50}}})),enabled:status.data?.available===true,refetchInterval:5000});
  const coverage=useQuery({queryKey:['native-cache-coverage',selected],queryFn:()=>body(api.POST('/api/v1/native-cache/coverage',{body:selected!})),enabled:selected!==null});
  const action=useMutation({mutationFn:({entry,operation}:{entry:Entry;operation:Schemas['NativeCacheOperation']})=>body(api.POST('/api/v1/native-cache/files',{body:{entry,operation}})),onSuccess:async changed=>{if(!changed)toast.info(status.data?.recovery_mode ? 'Cache recovery mode keeps payloads read only.' : 'The file is active, pinned, or already removed.');setSelected(null);await queryClient.invalidateQueries({queryKey:['native-cache-files']});await queryClient.invalidateQueries({queryKey:['native-cache-status']});},onError:error=>toast.error(error.message)});
  return <><AlertDialog open={evict!==null} onOpenChange={open=>{if(!open)setEvict(null);}}><AlertDialogContent><AlertDialogHeader><AlertDialogTitle>Evict cached file?</AlertDialogTitle><AlertDialogDescription>Future playback will fetch this file from its Usenet source. Active or pinned files stay protected.</AlertDialogDescription></AlertDialogHeader><AlertDialogFooter><AlertDialogCancel>Cancel</AlertDialogCancel><AlertDialogAction onClick={()=>{if(evict)action.mutate({entry:evict,operation:'evict'});setEvict(null);}}>Evict</AlertDialogAction></AlertDialogFooter></AlertDialogContent></AlertDialog>
  <SettingsPanel title="Cached files">
    {status.error&&<Failure error={status.error}/>}
    {status.data?.reason&&<p className="text-sm text-destructive">{status.data.reason}</p>}
    {!status.data?.available?<p className="text-sm text-muted-foreground">The final-media cache is inactive. Enable it, save settings, and restart to use it.</p>:<>
      <p className="text-sm text-muted-foreground">{status.data.stats?.files??0} files · {bytes(status.data.stats?.bytes??0)} verified bytes. Coverage is a catalogue snapshot; reads verify stored bytes. Health checks automatically pin a complete copy of a damaged source. Unpinning that copy allows eviction.</p>
      {status.data.stats?.io && <div className="space-y-1 rounded-lg border p-3 text-xs text-muted-foreground"><p className="text-sm font-medium text-foreground">Storage I/O since restart</p><p>{bytes(status.data.stats.io.verified_read_bytes)} verified block reads · {bytes(status.data.stats.io.synced_write_bytes)} synced writes</p><p>{status.data.stats.io.active} running · {status.data.stats.io.queued} queued · {status.data.stats.io.available_buffer_blocks} buffer blocks available · {status.data.stats.io.failed} failed requests</p><p>Tracks physical cache work, including work still running after a caller times out. Synced writes can precede catalogue publication. These totals are separate from bytes sent to players and provider traffic.</p></div>}
      <NativeCacheTools />
      <NativeCacheRecovery folders={folders}/>
      {entries.error&&<Failure error={entries.error}/>}
      <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr className="border-b text-left text-muted-foreground"><th className="p-2">File</th><th className="p-2">Coverage</th><th className="p-2">Actions</th></tr></thead><tbody>
        {entries.data?.map(entry=><tr key={`${entry.item}-${entry.revision}`} className="border-b"><td className="p-2 font-mono text-xs">{entry.item.slice(0,8)} · {entry.revision.slice(0,8)}</td><td className="p-2">{bytes(entry.cached_bytes)} / {bytes(entry.size_bytes)}</td><td className="flex gap-1 p-2"><Button type="button" variant="ghost" size="sm" onClick={()=>setSelected(entry)}>Ranges</Button><Button type="button" variant="ghost" size="sm" disabled={action.isPending} onClick={()=>action.mutate({entry,operation:entry.pinned?'unpin':'pin'})}>{entry.pinned?'Unpin':'Pin'}</Button><Button type="button" variant="ghost" size="sm" disabled={entry.pinned||action.isPending} onClick={()=>setEvict(entry)}>Evict</Button></td></tr>)}
      </tbody></table></div>
      <div className="flex gap-2"><Button type="button" variant="outline" size="sm" disabled={offset===0} onClick={()=>setOffset(Math.max(0,offset-50))}>Previous</Button><Button type="button" variant="outline" size="sm" disabled={(entries.data?.length??0)<50} onClick={()=>setOffset(offset+50)}>Next</Button></div>
      {selected&&<div className="space-y-1 rounded-lg border p-3"><p className="text-sm font-medium">Verified ranges (end exclusive)</p>{coverage.error&&<Failure error={coverage.error}/>}<p className="break-words font-mono text-xs">{coverage.data?.map(r=>`${r.start}–${r.end}`).join(', ')||'No verified ranges'}</p><NativeCacheProbe key={`${selected.item}-${selected.revision}`} entry={selected}/></div>}
    </>}
  </SettingsPanel></>;
}

function LegacyCacheImport() {
  type Request = Schemas["LegacyCacheImport"];
  const saved = () => {
    try { return JSON.parse(localStorage.getItem("legacy-cache-import") || "null") as {request:Request;bytes:number;rejected:number;complete:boolean}|null; }
    catch { return null; }
  };
  const [progress,setProgress] = useState(saved);
  const [path,setPath] = useState(progress?.request.metadata_path || "");
  const [folders,setFolders] = useState(progress?.request.folders || [{id:"",path:""}]);
  const [running,setRunning] = useState(false);
  const [failure,setFailure] = useState<string|null>(null);
  const active = useRef(false);
  const queryClient = useQueryClient();
  useEffect(() => () => { active.current = false; }, []);
  const run = async () => {
    if (active.current) return;
    active.current=true; setRunning(true);setFailure(null);
    let state = progress || {request:{metadata_path:path,folders,cursor:{after_key:"",offset:-1}},bytes:0,rejected:0,complete:false};
    try {
      while(active.current && !state.complete) {
        const report = await body(api.POST("/api/v1/native-cache/import-legacy",{body:state.request}));
        state = {...state,request:{...state.request,cursor:report.cursor},bytes:state.bytes+report.processed_bytes,rejected:state.rejected+report.rejected_entries+report.rejected_blocks,complete:report.complete};
        localStorage.setItem("legacy-cache-import",JSON.stringify(state));setProgress(state);
        void queryClient.invalidateQueries({queryKey:["native-cache-files"]});
        if(report.paused) {setFailure("Import paused because cache storage or buffers are unavailable. Free space or wait for playback to finish, then resume.");break;}
      }
    } catch(error) {setFailure(error instanceof Error ? error.message : "Legacy cache import failed");}
    finally {active.current=false;setRunning(false);}
  };
  return <SettingsPanel title="Import a 1.x native cache">
    <p className="text-sm text-muted-foreground">Import the C# installation first. Keep 1.x stopped while copying its cache. Verified blocks are copied into the separate Rust cache; the old cache remains available for rollback. The destination needs enough free space. Source revisions that cannot be proven are skipped.</p>
    <Field id="legacy-native-metadata" label="1.x native metadata directory"><Input id="legacy-native-metadata" value={path} disabled={running||!!progress} onChange={event=>setPath(event.target.value)} placeholder="/legacy-config/native-cache-metadata" /></Field>
    {folders.map((folder,index)=><div key={index} className="grid gap-2 sm:grid-cols-2">
      <Field id={`legacy-folder-${index}-id`} label="1.x folder ID"><Input id={`legacy-folder-${index}-id`} value={folder.id} disabled={running||!!progress} onChange={event=>setFolders(folders.map((value,i)=>i===index?{...value,id:event.target.value}:value))}/></Field>
      <Field id={`legacy-folder-${index}-path`} label="1.x cache folder path"><Input id={`legacy-folder-${index}-path`} value={folder.path} disabled={running||!!progress} onChange={event=>setFolders(folders.map((value,i)=>i===index?{...value,path:event.target.value}:value))}/></Field>
    </div>)}
    {!progress&&<Button type="button" variant="outline" disabled={running||folders.length>=32} onClick={()=>setFolders([...folders,{id:"",path:""}])}>Add source folder</Button>}
    {progress&&<p className="text-sm" role="status">{progress.complete?"Import complete":"Import progress saved"} · {bytes(progress.bytes)} processed · {progress.rejected} rejected entries or blocks.</p>}
    {failure&&<p className="text-sm text-destructive" role="alert">{failure}</p>}
    <div className="flex flex-wrap gap-2">
      <Button type="button" disabled={running||progress?.complete||!path||folders.some(folder=>!folder.id||!folder.path)} onClick={()=>void run()}>{progress?"Resume import":"Copy verified cache"}</Button>
      {running&&<Button type="button" variant="outline" onClick={()=>{active.current=false;}}>Pause after this batch</Button>}
      {progress&&!running&&<Button type="button" variant="outline" onClick={()=>{localStorage.removeItem("legacy-cache-import");setProgress(null);setFailure(null);}}>Start another import</Button>}
    </div>
  </SettingsPanel>;
}
