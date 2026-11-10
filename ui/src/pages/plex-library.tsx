import { Link, useSearchParams } from "react-router";
import { PageHeader } from "../ui";
import { Button } from "@/components/ui/button";
import { PlexConnections } from "./plex";
import { PlexLibraryBrowser } from "./plex-library-browser";
export function PlexLibrary() {
  const [params, setParams] = useSearchParams();
  const connections = params.get("tab") === "connections";
  const tab = (value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set("tab", value);
    else next.delete("tab");
    setParams(next);
  };
  return (
    <>
      <PageHeader
        title="Plex Library"
        description="Browse movies, shows and library files."
      >
        <Button variant="outline" asChild>
          <Link to="/smart-prefetch">Smart Prefetch</Link>
        </Button>
      </PageHeader>
      <nav
        aria-label="Plex Library views"
        className="mb-6 flex flex-wrap gap-2"
      >
        <Button
          variant={!connections ? "secondary" : "ghost"}
          aria-current={!connections ? "page" : undefined}
          onClick={() => tab("")}
        >
          Browse library
        </Button>
        <Button
          variant={connections ? "secondary" : "ghost"}
          aria-current={connections ? "page" : undefined}
          onClick={() => tab("connections")}
        >
          Connections and path mappings
        </Button>
        <Button variant="ghost" asChild>
          <Link to="/library">Local media index</Link>
        </Button>
      </nav>
      {connections ? <PlexConnections /> : <PlexLibraryBrowser />}
    </>
  );
}
export function PlexBrowser() {
  return <PlexLibraryBrowser />;
}
