import { useEffect, useState } from "react";
import { z } from "zod";
import type { CacheRange } from "~/components/cache-range-map";
import { withUrlBase } from "~/utils/url-base";

export type FileCacheMap = {
  available: true;
  length: number;
  cachedBytes: number;
  ranges: CacheRange[];
  complete: boolean;
};
const cacheResponse = z.discriminatedUnion("available", [
  z.object({ available: z.literal(false) }),
  z.object({
    available: z.literal(true),
    length: z.number().positive(),
    cachedBytes: z.number().nonnegative(),
    ranges: z.array(z.object({ offset: z.number().nonnegative(), count: z.number().positive() })),
    complete: z.boolean(),
  }),
]);
type CacheState = {
  itemId: string | null;
  data: FileCacheMap | null;
  error: string | null;
};

/** Refresh only the open file; never reuse another file's coverage or range map. */
export function useFileCache(itemId: string | null, refreshMs = 5000) {
  const [state, setState] = useState<CacheState | null>(null);
  useEffect(() => {
    if (!itemId) return;
    const abort = new AbortController();
    let refresh: ReturnType<typeof setTimeout> | undefined;
    async function load() {
      try {
        const query = new URLSearchParams({ itemId: itemId! });
        const response = await fetch(withUrlBase(`/api/native-cache/file-ranges?${query}`), {
          signal: abort.signal,
        });
        if (!response.ok) throw new Error("Cache ranges could not be loaded.");
        const data = cacheResponse.parse(await response.json());
        if (!abort.signal.aborted)
          setState({ itemId, data: data.available ? data : null, error: null });
      } catch {
        if (!abort.signal.aborted)
          setState({ itemId, data: null, error: "Cache ranges could not be loaded." });
      } finally {
        if (!abort.signal.aborted) refresh = setTimeout(() => void load(), refreshMs);
      }
    }
    void load();
    return () => {
      abort.abort();
      clearTimeout(refresh);
    };
  }, [itemId, refreshMs]);
  const current = itemId && state?.itemId === itemId ? state : null;
  return {
    loading: !!itemId && current === null,
    data: current?.data ?? null,
    error: current?.error ?? null,
  };
}
