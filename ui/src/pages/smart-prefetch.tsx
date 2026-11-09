import { Link, useSearchParams } from "react-router";
import { Button } from "@/components/ui/button";
import { PageHeader } from "../ui";
import { SmartPrefetchSettings } from "./smart-prefetch-settings";
import { PrefetchPanel } from "./prefetch";
export function SmartPrefetch() {
  const [params, setParams] = useSearchParams();
  const chosen = params.get("tab");
  const section =
    chosen === "predictions" || chosen === "settings" ? chosen : "activity";
  return (
    <>
      <PageHeader
        title="Smart Prefetch"
        description="Warming activity, Plex predictions and the policies that fill your native cache."
      >
        <Button variant="outline" asChild>
          <Link to="/native-cache">Native Cache</Link>
        </Button>
      </PageHeader>
      <nav
        className="mb-6 flex flex-wrap gap-2"
        aria-label="Smart Prefetch views"
      >
        {(
          [
            ["activity", "Activity and history"],
            ["predictions", "Predictions"],
            ["settings", "Settings"],
          ] as const
        ).map(([value, label]) => (
          <Button
            key={value}
            variant={section === value ? "secondary" : "ghost"}
            aria-current={section === value ? "page" : undefined}
            onClick={() => {
              const next = new URLSearchParams(params);
              next.set("tab", value);
              setParams(next);
            }}
          >
            {label}
          </Button>
        ))}
      </nav>
      {section === "settings" ? (
        <SmartPrefetchSettings />
      ) : (
        <PrefetchPanel section={section} />
      )}
    </>
  );
}
