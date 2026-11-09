import { useState } from "react";
import { Link } from "react-router";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import type { Schemas } from "../api/client";
import { bytes } from "../format";
import { useFileCoverage } from "./prefetch-coverage";
import { rangeCoverage } from "./prefetch-activity-model";
type Prediction = Schemas["PlexPrediction"];
const labels: Record<string, string> = {
  all: "All cache states",
  fully: "Fully cached",
  partial: "Partially cached",
  uncached: "Not cached",
  unmapped: "Source not mapped",
  unknown: "Coverage unknown",
};
const sourceLabels: Record<string, string> = {
  playback: "Playing file",
  next_episode: "Next episode",
  partial_movie: "Partially watched",
  hub: "Hub source",
  collection: "Collection source",
  library: "Library source",
};
export function PredictionResults({
  predictions,
}: {
  predictions: Prediction[];
}) {
  const [search, setSearch] = useState(""),
    [viewer, setViewer] = useState("all"),
    [source, setSource] = useState("all"),
    [filter, setFilter] = useState("all"),
    [page, setPage] = useState(0),
    [check, setCheck] = useState(false);
  const groupMap = new Map<string, Prediction[]>();
  for (const p of predictions) {
    const key =
      p.item && p.revision
        ? `${p.item}:${p.revision}`
        : `${p.server}:${p.rating_key}`;
    const group = groupMap.get(key) ?? [];
    group.push(p);
    groupMap.set(key, group);
  }
  const groups = [...groupMap.entries()].map(([key, values]) => ({
    key,
    p: values[0]!,
    values,
  }));
  const name = (p: Prediction) =>
    p.playback?.find((v) => v.viewer_name)?.viewer_name ||
    `Plex profile ${p.viewer}`;
  const matching = groups.filter(
    (g) =>
      (!search || g.p.title.toLowerCase().includes(search.toLowerCase())) &&
      (viewer === "all" || g.values.some((p) => name(p) === viewer)) &&
      (source === "all" || g.values.some((p) => p.source === source)),
  );
  const targets = matching.flatMap((g) =>
    g.p.item && g.p.revision
      ? [{ item: g.p.item, revision: g.p.revision, size: g.p.size }]
      : [],
  );
  const coverage = useFileCoverage(targets, check || filter !== "all");
  const cacheState = (p: Prediction) => {
    if (!p.item || !p.revision) return "unmapped";
    const r = coverage.results.get(`${p.item}:${p.revision}`);
    if (!r) return "pending";
    if (r.ranges == null) return "unknown";
    const n = rangeCoverage({ ranges: [{ start: 0, end: p.size }] }, r.ranges);
    return n >= p.size && p.size > 0 ? "fully" : n > 0 ? "partial" : "uncached";
  };
  const filtered = matching.filter(
      (g) => filter === "all" || cacheState(g.p) === filter,
    ),
    safePage = Math.min(page, Math.max(0, Math.ceil(filtered.length / 25) - 1));
  const select = (
    label: string,
    value: string,
    options: [string, string][],
    onChange: (v: string) => void,
  ) => (
    <select
      aria-label={label}
      value={value}
      onChange={(e) => {
        onChange(e.target.value);
        setPage(0);
      }}
      className="h-8 min-w-0 rounded-md border bg-background px-2 text-sm"
    >
      {options.map(([v, l]) => (
        <option key={v} value={v}>
          {l}
        </option>
      ))}
    </select>
  );
  return (
    <section aria-label="Prediction results" className="space-y-3">
      <div className="flex flex-wrap gap-2 rounded-lg border bg-card p-3">
        <Input
          type="search"
          aria-label="Search predictions"
          placeholder="Search predicted files…"
          className="min-w-40 flex-1"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setPage(0);
          }}
        />
        {select(
          "Filter prediction viewer",
          viewer,
          [
            ["all", "All Plex profiles"],
            ...[...new Set(predictions.map(name))]
              .sort()
              .map((n) => [n, n] as [string, string]),
          ],
          setViewer,
        )}
        {select(
          "Filter prediction source",
          source,
          [
            ["all", "All prediction sources"],
            ...[...new Set(predictions.map((p) => p.source))].map(
              (s) => [s, sourceLabels[s] ?? s] as [string, string],
            ),
          ],
          setSource,
        )}
        {select(
          "Filter prediction cache status",
          filter,
          Object.entries(labels),
          setFilter,
        )}
        <Button
          size="sm"
          variant="outline"
          onClick={() => {
            setCheck(true);
            coverage.refresh();
          }}
          disabled={coverage.pending}
        >
          Check cache coverage
        </Button>
        <Button
          size="sm"
          variant="ghost"
          onClick={() => {
            setSearch("");
            setViewer("all");
            setSource("all");
            setFilter("all");
            setPage(0);
          }}
        >
          Clear filters
        </Button>
      </div>
      <div className="flex flex-wrap gap-2">
        {Object.entries(labels).map(([key, label]) => (
          <Button
            size="sm"
            variant={filter === key ? "secondary" : "outline"}
            aria-pressed={filter === key}
            key={key}
            onClick={() => {
              setFilter(key);
              setPage(0);
            }}
          >
            {label}
            {key === "all"
              ? ` · ${matching.length}`
              : check || filter !== "all"
                ? ` · ${matching.filter((g) => cacheState(g.p) === key).length}`
                : ""}
          </Button>
        ))}
      </div>
      {coverage.pending && (
        <p role="status" className="text-xs text-muted-foreground">
          Checking current coverage · {coverage.results.size} of{" "}
          {coverage.total} files. A completed warming attempt does not guarantee
          current coverage.
        </p>
      )}
      <div className="overflow-hidden rounded-lg border bg-card">
        {filtered.slice(safePage * 25, (safePage + 1) * 25).map((g) => {
          const p = g.p,
            cached = cacheState(p);
          return (
            <article
              key={g.key}
              className="space-y-2 border-b p-4 last:border-0"
            >
              <div className="flex flex-wrap justify-between gap-2">
                <p className="min-w-0 break-words text-sm font-medium">
                  {p.item ? (
                    <Link
                      className="hover:text-primary"
                      to={`/files/${p.item}`}
                    >
                      {p.title}
                    </Link>
                  ) : (
                    p.title
                  )}
                </p>
                <Badge
                  variant="outline"
                  className={cached === "fully" ? "text-success-text" : ""}
                >
                  {cached === "pending"
                    ? "Coverage not checked"
                    : labels[cached]}
                </Badge>
              </div>
              <div className="flex flex-wrap gap-1">
                {[
                  ...new Set(
                    g.values.map((p) => sourceLabels[p.source] ?? p.source),
                  ),
                ].map((s) => (
                  <Badge variant="secondary" key={s}>
                    {s}
                  </Badge>
                ))}
                {[...new Set(g.values.map(name))].map((v) => (
                  <Badge variant="outline" key={v}>
                    {v}
                  </Badge>
                ))}
              </div>
              <p className="text-xs text-muted-foreground">
                {p.warning ||
                  `${bytes(p.ranges.reduce((n, r) => n + r.end - r.start, 0))} selected for warming · ${bytes(p.size)} file`}
              </p>
            </article>
          );
        })}
        {!filtered.length && (
          <div className="p-8 text-center text-sm text-muted-foreground">
            No predictions match these filters.
          </div>
        )}
      </div>
      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
        <p>
          {filtered.length} predicted files · Page {safePage + 1} of{" "}
          {Math.max(1, Math.ceil(filtered.length / 25))}
        </p>
        <div className="flex gap-2">
          <Button
            size="sm"
            variant="outline"
            disabled={!safePage}
            onClick={() => setPage(safePage - 1)}
          >
            Previous predictions
          </Button>
          <Button
            size="sm"
            variant="outline"
            disabled={(safePage + 1) * 25 >= filtered.length}
            onClick={() => setPage(safePage + 1)}
          >
            Next predictions
          </Button>
        </div>
      </div>
    </section>
  );
}
