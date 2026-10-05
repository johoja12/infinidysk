import { formatFileSize } from "~/utils/file-size";

export type CacheRange = { offset: number; count: number };
function percentage(value: number, length: number): number {
  return length > 0 ? Math.min(100, Math.max(0, (value / length) * 100)) : 0;
}

export function CacheRangeMap({
  file,
  ranges,
  complete,
}: {
  file: { length: number };
  ranges: CacheRange[];
  complete: boolean;
}) {
  const ordered = [...ranges].sort((a, b) => a.offset - b.offset);
  const gaps: { start: number; end: number }[] = [];
  let position = 0;
  for (const range of ordered) {
    if (range.offset > position) gaps.push({ start: position, end: range.offset });
    position = Math.max(position, range.offset + range.count);
  }
  if (complete && position < file.length) gaps.push({ start: position, end: file.length });
  return (
    <>
      <div
        className="relative mt-4 h-4 overflow-hidden rounded bg-base-content/15"
        role="img"
        aria-label={`${ordered.length} verified ranges; ${complete ? `${gaps.length} known gaps` : "more ranges available"}`}
      >
        {ordered.map((range) => (
          <span
            key={range.offset}
            className="absolute inset-y-0 bg-primary"
            style={{
              left: `${percentage(range.offset, file.length)}%`,
              width: `${percentage(range.count, file.length)}%`,
            }}
          />
        ))}
      </div>
      <div className="mt-2 flex gap-4 text-xs text-base-content/55">
        <span>
          <i className="mr-1 inline-block h-2 w-2 bg-primary" />
          Verified
        </span>
        <span>
          <i className="mr-1 inline-block h-2 w-2 bg-base-content/25" />
          Gap or unloaded
        </span>
      </div>
      {complete && gaps.length > 0 && (
        <div className="mt-4">
          <p className="text-xs font-semibold uppercase tracking-wider text-base-content/55">
            Known gaps
          </p>
          <ul className="mt-1 max-h-40 divide-y divide-base-content/10 overflow-y-auto text-xs">
            {gaps.slice(0, 20).map((gap) => (
              <li className="py-1" key={gap.start}>
                {formatFileSize(gap.start)} – {formatFileSize(gap.end)}
              </li>
            ))}
          </ul>
          {gaps.length > 20 && (
            <p className="text-xs text-base-content/50">Showing first 20 of {gaps.length} gaps.</p>
          )}
        </div>
      )}
    </>
  );
}
