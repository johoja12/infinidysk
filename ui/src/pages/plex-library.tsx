import { lazy, Suspense, useEffect, useState } from "react";
import { Link, Navigate, useLocation, useSearchParams } from "react-router";
import { Failure, Loading, PageHeader } from "../ui";
import { useSettings } from "../settings";
import { libraryHref, type LibrarySource } from "../library-navigation";
import { Button } from "@/components/ui/button";
const PlexLibraryBrowser = lazy(() =>
  import("./plex-library-browser").then((m) => ({
    default: m.PlexLibraryBrowser,
  })),
);
const PlexConnections = lazy(() =>
  import("./plex").then((m) => ({ default: m.PlexConnections })),
);

const LocalCatalogue = lazy(() =>
  import("./catalogue").then((m) => ({ default: m.Catalogue })),
);

export function LibraryPage() {
  const settings = useSettings();
  const [params] = useSearchParams();
  const requested = params.get("source");
  const hasPlex =
    settings.data?.settings.plex.servers.some((s) => s.enabled) ?? false;
  const source: LibrarySource =
    requested === "plex" || requested === "local" || requested === "connections"
      ? requested
      : hasPlex
        ? "plex"
        : "local";
  const ready = !settings.isPending;
  const [visitedLocal, setVisitedLocal] = useState(false);
  useEffect(() => {
    if (ready && source === "local") setVisitedLocal(true);
  }, [ready, source]);

  return (
    <>
      <PageHeader
        title="Library"
        description="Browse Plex and local files from one place"
      >
        <Button variant="outline" asChild>
          <Link to="/smart-prefetch">Smart Prefetch</Link>
        </Button>
      </PageHeader>
      <nav aria-label="Library views" className="mb-4 flex flex-wrap gap-2">
        {(
          [
            ["plex", "Browse media"],
            ["local", "Local files"],
            ["connections", "Connections and path mappings"],
          ] as const
        ).map(([value, label]) => (
          <Button
            key={value}
            variant={source === value ? "secondary" : "ghost"}
            asChild
          >
            <Link
              to={libraryHref(params, value)}
              aria-current={source === value ? "page" : undefined}
            >
              {label}
            </Link>
          </Button>
        ))}
      </nav>
      {settings.error && <Failure error={settings.error} className="mb-4" />}
      {!ready ? (
        <Loading />
      ) : (
        <>
          <p className="mb-5 text-sm text-muted-foreground">
            {source === "plex"
              ? "Movies, shows and local files, with Plex metadata when available."
              : source === "local"
                ? "Files in your configured library folders, including files Plex has not indexed."
                : "Manage Plex accounts, servers and library path mappings."}
          </p>
          <Suspense fallback={<Loading />}>
            {source === "plex" && <PlexLibraryBrowser />}
            {source === "connections" && <PlexConnections />}
          </Suspense>
        </>
      )}
      {(visitedLocal || (ready && source === "local")) && (
        <div hidden={source !== "local"}>
          <Suspense fallback={<Loading />}>
            <LocalCatalogue embedded active={source === "local"} />
          </Suspense>
        </div>
      )}
    </>
  );
}

/** Keep existing Plex bookmarks, filters and fragment links working. */
export function LegacyPlexLibraryRedirect() {
  const location = useLocation();
  const next = new URLSearchParams(location.search);
  next.set(
    "source",
    next.get("tab") === "connections" ? "connections" : "plex",
  );
  next.delete("tab");
  return (
    <Navigate
      replace
      to={{ pathname: "/library", search: `?${next}`, hash: location.hash }}
    />
  );
}

export { LibraryPage as PlexLibrary };
export function PlexBrowser() {
  return (
    <Suspense fallback={<Loading />}>
      <PlexLibraryBrowser />
    </Suspense>
  );
}
