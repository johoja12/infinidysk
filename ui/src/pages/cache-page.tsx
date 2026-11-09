import { useQuery } from "@tanstack/react-query";
import { Link, useSearchParams } from "react-router";
import { api, body } from "../api/client";
import { useSettings } from "../settings";
import { Failure, Loading, PageHeader } from "../ui";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { NativeCacheBrowser } from "./native-cache";
import { OriginalCacheBrowser } from "./original-cache-browser";
import {
  OriginalCacheOverview,
  useOriginalCacheOverview,
} from "./original-cache-overview";
import { CacheEvictions } from "./cache-evictions";
import { NativeCacheTools } from "./native-cache-tools";
export function CachePage() {
  const settings = useSettings(),
    [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "overview",
    chosen = ["files", "evictions", "diagnostics"].includes(tab)
      ? tab
      : "overview";
  const status = useQuery({
    queryKey: ["native-cache-status"],
    queryFn: () => body(api.GET("/api/v1/native-cache")),
    refetchInterval: 5000,
  });
  const hasOriginal = !!settings.data?.settings.native_cache.legacy;
  const overview = useOriginalCacheOverview(hasOriginal);
  const navigate = (value: string) => {
    const next = new URLSearchParams(params);
    next.set("tab", value);
    setParams(next);
  };
  const mode = !status.data?.available
    ? "Unavailable"
    : status.data.recovery_mode
      ? "Recovery hold"
      : hasOriginal
        ? overview.data?.writable
          ? "Compatible writes"
          : "Read only"
        : "Native storage";
  return (
    <>
      <PageHeader
        title="Native Cache"
        description="Storage, cached files and eviction history."
      >
        <Button variant="outline" asChild>
          <Link to="/settings/native-cache">Cache settings</Link>
        </Button>
      </PageHeader>
      <div className="space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex flex-wrap gap-2">
            <Badge variant="secondary">{mode}</Badge>
            {hasOriginal && overview.data && (
              <Badge variant="outline">
                {overview.data.folders.some((f) => f.eviction_enabled)
                  ? "Eviction permitted"
                  : "Eviction disabled"}
              </Badge>
            )}
            <span className="text-xs text-muted-foreground">
              Existing cache payloads stay in place.
            </span>
          </div>
          <Button
            type="button"
            size="sm"
            variant="outline"
            onClick={() => {
              void status.refetch();
              if (hasOriginal) void overview.refetch();
            }}
          >
            Refresh cache
          </Button>
        </div>
        {settings.error && <Failure error={settings.error} />}{" "}
        {status.error && <Failure error={status.error} />}{" "}
        {status.data?.reason && (
          <Failure error={new Error(status.data.reason)} />
        )}{" "}
        {status.data?.recovery_mode && (
          <p role="status" className="text-sm text-warning-text">
            Cache recovery is holding writes and eviction. Existing records
            remain available for inspection.
          </p>
        )}
        <nav
          role="tablist"
          aria-label="Native cache views"
          className="flex flex-wrap gap-1 border-b pb-2"
        >
          {[
            ["overview", "Overview"],
            ["files", "Files"],
            ["evictions", "Evictions"],
            ["diagnostics", "Diagnostics"],
          ].map(([value, label]) => (
            <Button
              key={value}
              role="tab"
              aria-selected={chosen === value}
              variant={chosen === value ? "secondary" : "ghost"}
              onClick={() => navigate(value!)}
            >
              {label}
            </Button>
          ))}
        </nav>
        {settings.isPending ? (
          <Loading />
        ) : (
          settings.data && (
            <>
              {chosen === "overview" &&
                (hasOriginal ? (
                  <OriginalCacheOverview
                    status={status.data}
                    onFiles={() => navigate("files")}
                  />
                ) : (
                  <NativeCacheBrowser
                    folders={settings.data.settings.native_cache.folders}
                  />
                ))}
              {chosen === "files" && (
                <>
                  {hasOriginal && <OriginalCacheBrowser />}
                  {settings.data.settings.native_cache.folders.length > 0 && (
                    <details className="rounded-lg border p-4">
                      <summary className="cursor-pointer text-sm font-medium">
                        Additional cache stores
                      </summary>
                      <div className="mt-4">
                        <NativeCacheBrowser
                          folders={settings.data.settings.native_cache.folders}
                        />
                      </div>
                    </details>
                  )}
                  {!hasOriginal &&
                    !settings.data.settings.native_cache.folders.length && (
                      <p className="text-sm text-muted-foreground">
                        No cache folders are configured.
                      </p>
                    )}
                </>
              )}
              {chosen === "evictions" && (
                <CacheEvictions hasOriginal={hasOriginal} />
              )}
              {chosen === "diagnostics" && (
                <div className="space-y-3">
                  <h2 className="text-lg font-medium">Storage diagnostics</h2>
                  <p className="text-sm text-muted-foreground">
                    Check storage identity, permission and free space without
                    downloading media. File hash checks are available from Files
                    → Ranges.
                  </p>
                  <NativeCacheTools showHistory={false} />
                </div>
              )}
            </>
          )
        )}
      </div>
    </>
  );
}
