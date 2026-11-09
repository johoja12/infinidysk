import { bytes } from "../format";
type Range = { start: number; end: number };
export function CacheRangeMap({
  size,
  ranges,
}: {
  size: number;
  ranges: Range[];
}) {
  const ordered = [...ranges].sort((a, b) => a.start - b.start);
  const gaps: Range[] = [];
  let position = 0;
  for (const r of ordered) {
    if (r.start > position) gaps.push({ start: position, end: r.start });
    position = Math.max(position, r.end);
  }
  if (position < size) gaps.push({ start: position, end: size });
  // A single SVG path keeps fragmented catalogues from creating thousands of DOM nodes.
  const path = ordered
    .map(
      (r) =>
        `M${Math.max(0, (r.start / Math.max(size, 1)) * 1000)},0h${Math.max(0, ((r.end - r.start) / Math.max(size, 1)) * 1000)}v16h-${Math.max(0, ((r.end - r.start) / Math.max(size, 1)) * 1000)}z`,
    )
    .join(" ");
  return (
    <div className="space-y-3">
      <svg
        viewBox="0 0 1000 16"
        preserveAspectRatio="none"
        role="img"
        aria-label={`${ordered.length} cached ranges; ${gaps.length} known gaps`}
        className="h-4 w-full overflow-hidden rounded bg-muted"
      >
        <path d={path} className="fill-primary" />
      </svg>
      <p className="text-xs text-muted-foreground">
        <span className="mr-1 inline-block size-2 rounded-sm bg-primary" />
        Catalogue coverage{" "}
        <span className="ml-4 mr-1 inline-block size-2 rounded-sm bg-muted" />
        Gap · {ordered.length.toLocaleString()} ranges /{" "}
        {gaps.length.toLocaleString()} gaps
      </p>
      {gaps.length > 0 && (
        <details>
          <summary className="cursor-pointer text-xs font-medium">
            Known gaps
          </summary>
          <ul className="mt-2 max-h-40 space-y-1 overflow-auto text-xs tabular-nums">
            {gaps.slice(0, 100).map((r) => (
              <li key={r.start}>
                {bytes(r.start)}–{bytes(r.end)} · {bytes(r.end - r.start)}
              </li>
            ))}
          </ul>
          {gaps.length > 100 && (
            <p className="text-xs text-muted-foreground">
              First 100 of {gaps.length.toLocaleString()} gaps.
            </p>
          )}
        </details>
      )}
    </div>
  );
}
export function CacheMeter({
  percent,
  label,
}: {
  percent: number;
  label: string;
}) {
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuenow={Math.round(Math.max(0, Math.min(100, percent)))}
      aria-valuemin={0}
      aria-valuemax={100}
      className="h-1.5 overflow-hidden rounded-full bg-muted"
    >
      <div
        className="h-full rounded-full bg-primary"
        style={{ width: `${Math.max(0, Math.min(100, percent))}%` }}
      />
    </div>
  );
}
