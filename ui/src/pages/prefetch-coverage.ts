import { useEffect, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import { api, body } from "../api/client";
type Target = { item: string; revision: string; size: number };
export function useFileCoverage(targets: Target[], enabled: boolean) {
  const unique = [
    ...new Map(targets.map((j) => [`${j.item}:${j.revision}`, j])).values(),
  ].sort((a, b) =>
    `${a.item}:${a.revision}`.localeCompare(`${b.item}:${b.revision}`),
  );
  const [epoch, setEpoch] = useState(0);
  const scope = enabled
    ? `${epoch}:` +
      unique.map((j) => `${j.item}:${j.revision}:${j.size}`).join("|")
    : "";
  const [batch, setBatch] = useState({ scope: "", count: 0 });
  const count = batch.scope === scope ? batch.count : 0;
  const selected = enabled ? unique.slice(0, (count + 1) * 4) : [];
  const queries = useQueries({
    queries: selected.map((j, i) => ({
      queryKey: [
        "prefetch-current-coverage",
        j.item,
        j.revision,
        j.size,
        epoch,
      ],
      queryFn: async ({ signal }: { signal: AbortSignal }) => {
        const d = await body(
          api.GET("/api/v1/files/{id}", {
            params: { path: { id: j.item } },
            signal,
          }),
        );
        return d.source_revision === j.revision &&
          d.entry.size === j.size &&
          d.cache_available &&
          !d.cache_warning
          ? d.cached_ranges
          : null;
      },
      enabled: i >= count * 4,
      staleTime: 15000,
      retry: false,
      refetchOnWindowFocus: false,
      refetchOnReconnect: false,
    })),
  });
  const pending = enabled && queries.some((q) => q.isPending || q.isFetching);
  useEffect(() => {
    if (enabled && !pending && selected.length < unique.length)
      setBatch({ scope, count: count + 1 });
  }, [enabled, pending, selected.length, unique.length, scope, count]);
  useEffect(() => {
    if (
      !enabled ||
      pending ||
      selected.length !== unique.length ||
      !unique.length
    )
      return;
    const timer = setTimeout(() => setEpoch((value) => value + 1), 30000);
    return () => clearTimeout(timer);
  }, [enabled, pending, selected.length, unique.length, scope]);
  const results = new Map(
    selected.flatMap((j, i) => {
      const q = queries[i];
      return !q || q.isPending
        ? []
        : [
            [
              `${j.item}:${j.revision}`,
              { ranges: q.isError ? null : (q.data ?? null) },
            ] as const,
          ];
    }),
  );
  return {
    results,
    refresh: () => setEpoch((value) => value + 1),
    total: unique.length,
    pending: pending || (enabled && selected.length < unique.length),
  };
}
