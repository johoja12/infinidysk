export type LibrarySource = "plex" | "local" | "connections";

/** Retain source-specific filters when switching Library views. */
export function libraryHref(params: URLSearchParams, source: LibrarySource) {
  const next = new URLSearchParams(params);
  next.set("source", source);
  return `/library?${next}`;
}
