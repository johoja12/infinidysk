import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router";
import { useEffect } from "react";
import { useSettings } from "../settings";
import { RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { api, body } from "../api/client";
import { ago } from "../format";
import { Failure } from "../ui";
import { Button } from "@/components/ui/button";

export function PlexClassification() {
  const client = useQueryClient();
  const settings = useSettings().data?.settings;
  useEffect(() => { void client.invalidateQueries({ queryKey: ["plex-classification"] }); }, [client, settings]);
  const status = useQuery({
    queryKey: ["plex-classification"], queryFn: () => body(api.GET("/api/v1/library/plex")),
    refetchInterval: (q) => q.state.data?.refreshing ? 2000 : 30000,
  });
  const sync = useMutation({
    mutationFn: () => body(api.POST("/api/v1/library/plex")),
    onSuccess: (s) => {
      client.setQueryData(["plex-classification"], s);
      void client.invalidateQueries({ queryKey: ["file"] });
      if (s.error) toast.error(s.error); else toast.success("Plex classification synced");
    },
  });
  if (status.isError) return <Failure error={status.error} />;
  if (!status.data) return null;
  const s = status.data;
  return <div className="space-y-2 text-sm">
    <div className="flex flex-wrap items-center gap-2">
      <p className="text-muted-foreground">{!s.enabled ? "Plex classification is disabled." : s.refreshing ? "Syncing Plex classification…" : s.updated_at != null ? `${s.entries.toLocaleString()} Plex records${s.stale ? " · stale snapshot" : ""}.` : "Awaiting a complete Plex snapshot."}{s.updated_at != null && <> Updated {ago(s.updated_at)}.</>}</p>
      <Button variant="outline" size="sm" disabled={!s.enabled || !s.servers || s.refreshing || sync.isPending} onClick={() => sync.mutate()}><RefreshCw />Sync Plex metadata</Button>
    </div>
    {s.enabled && !s.servers && <p className="text-warning-text">Choose an enabled Plex server for matching.</p>}
    {s.error && <p className="text-warning-text break-words" role="status">{s.error}</p>}
    {sync.isError && <Failure error={sync.error} />}
    <p className="text-muted-foreground text-xs"><Link className="text-primary underline" to="/settings/library">Library settings</Link> select servers and enable read-only matching. Manage accounts in <Link className="text-primary underline" to="/settings/plex">Plex connections</Link>.</p>
  </div>;
}
