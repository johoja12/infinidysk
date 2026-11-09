import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router";
import { api, body } from "../api/client";
import { useSettings } from "../settings";
import { bytes } from "../format";
import { Empty, Failure, Loading, PageHeader, SettingsPanel } from "../ui";
import { Button } from "@/components/ui/button";
import { NativeCacheBrowser } from "./native-cache";
import { OriginalCacheBrowser } from "./original-cache-browser";
import { NativeCacheTools } from "./native-cache-tools";
export function CachePage() {
  const view = useSettings();
  const status = useQuery({
    queryKey: ["native-cache-status"],
    queryFn: () => body(api.GET("/api/v1/native-cache")),
    refetchInterval: 5000,
  });
  return (
    <>
      <PageHeader
        title="Native Cache"
        description="Verified media retained for playback and Smart Prefetch."
      >
        <Button variant="outline" asChild>
          <Link to="/settings/native-cache">Cache settings</Link>
        </Button>
      </PageHeader>
      <div className="space-y-6">
        {view.error && <Failure error={view.error} />}
        {status.error && <Failure error={status.error} />}
        {status.data?.reason && (
          <Failure error={new Error(status.data.reason)} />
        )}
        {status.data?.recovery_mode && (
          <p role="status" className="text-warning-text text-sm">
            Recovery mode holds writes and eviction.
          </p>
        )}
        {status.data?.stats && (
          <SettingsPanel title="Activity since restart">
            <p className="text-sm">
              {status.data.stats.hits.toLocaleString()}{" "}
              verified cached block reads ·{" "}
              {bytes(status.data.stats.legacy_written_bytes)} added to existing
              storage
            </p>
            <p className="text-xs text-muted-foreground">
              Hit counts describe verified blocks. Folder totals below describe
              saved catalogue coverage; payload bytes are verified when read.
            </p>
          </SettingsPanel>
        )}
        {view.isPending ? (
          <Loading />
        ) : view.data?.settings.native_cache.legacy ? (
          <OriginalCacheBrowser />
        ) : (
          <Empty title="No original cache configured">
            Configure existing storage in cache settings. Existing payloads stay
            in place.
          </Empty>
        )}
        {(view.data?.settings.native_cache.folders.length ?? 0) > 0 && (
          <NativeCacheBrowser
            folders={view.data!.settings.native_cache.folders}
          />
        )}
        {view.data?.settings.native_cache.folders.length === 0 && (
          <NativeCacheTools />
        )}
      </div>
    </>
  );
}
