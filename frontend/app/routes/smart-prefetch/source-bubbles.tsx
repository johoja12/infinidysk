export type JobSource = { label: string; category: string };

/**
 * Bubble colours per source category. Bubbles are soft and rounded so they stay visually
 * distinct from the solid job-state badge next to them.
 */
const categoryClass: Record<string, string> = {
  "plex-source": "badge-primary",
  "plex-realtime": "badge-accent",
  "plex-realtime-next": "badge-secondary",
  "plex-history": "badge-info",
  "plex-history-next": "badge-success",
  backfill: "badge-warning",
  "finish-watched": "badge-accent badge-outline",
  manual: "badge-neutral",
  read: "badge-ghost",
};

export function sourceBubbleClass(category: string): string {
  return categoryClass[category] ?? "badge-ghost";
}

/** Colour-coded bubbles naming a job's concrete sources: the first two, then "+N". */
export function SourceBubbles({
  sources,
  sourceCount,
  fallback,
}: {
  sources?: JobSource[] | null;
  sourceCount?: number | null;
  fallback: string;
}) {
  const list = sources && sources.length > 0 ? sources : [{ label: fallback, category: "other" }];
  const shown = list.slice(0, 2);
  const total = Math.max(sourceCount ?? list.length, list.length);
  const hidden = total - shown.length;
  return (
    <span className="inline-flex min-w-0 flex-wrap items-center gap-1">
      {shown.map((source) => (
        <span
          key={`${source.category}:${source.label}`}
          className={`badge badge-sm badge-soft h-auto max-w-full whitespace-normal break-words rounded-full py-1 ${sourceBubbleClass(source.category)}`}
          title={source.label}
          data-category={source.category}
        >
          {source.label}
        </span>
      ))}
      {hidden > 0 && (
        <span
          className="badge badge-sm badge-soft badge-ghost rounded-full"
          title={list
            .slice(shown.length)
            .map((source) => source.label)
            .join(", ")}
        >
          +{hidden}
        </span>
      )}
    </span>
  );
}
