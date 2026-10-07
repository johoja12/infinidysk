import { useEffect, useState } from "react";
import { withUrlBase } from "./url-base";

type Snapshot<T> = {
  refreshKey: unknown;
  ids: string;
  values: Record<string, T>;
  pending: boolean;
};

/** Page data renders first. Coverage is bounded and cancelled when the requested page changes. */
export function usePageCoverage<T>(
  path: string,
  parameter: string,
  ids: string[],
  refreshKey: unknown,
  batchSize: number,
  coalesceRefresh = false,
) {
  const idsKey = [...new Set(ids)].join(",");
  const [requestKey, setRequestKey] = useState(() => ({ value: refreshKey }));
  const [snapshot, setSnapshot] = useState<Snapshot<T> | null>(null);
  // Status polling must not continually cancel a slow history-coverage scan. Finish the
  // current batch sequence, then refresh against the newest status. Page changes still abort.
  const snapshotPending = snapshot?.pending ?? false;
  useEffect(() => {
    if (coalesceRefresh && !snapshotPending && !Object.is(requestKey.value, refreshKey))
      setRequestKey({ value: refreshKey });
  }, [coalesceRefresh, snapshotPending, requestKey, refreshKey]);
  const effectiveKey = coalesceRefresh ? requestKey.value : refreshKey;
  useEffect(() => {
    if (!idsKey || (coalesceRefresh && effectiveKey === null)) return;
    const controller = new AbortController();
    const values: Record<string, T> = {};
    setSnapshot({ refreshKey: effectiveKey, ids: idsKey, values, pending: true });
    void (async () => {
      const requested = idsKey.split(",");
      try {
        for (let offset = 0; offset < requested.length; offset += batchSize) {
          const query = new URLSearchParams({
            [parameter]: requested.slice(offset, offset + batchSize).join(","),
          });
          const response = await fetch(withUrlBase(`${path}?${query}`), {
            signal: AbortSignal.any([controller.signal, AbortSignal.timeout(30_000)]),
          });
          if (!response.ok) throw new Error("Coverage unavailable");
          const body = (await response.json()) as { coverage: Record<string, T> };
          if (controller.signal.aborted) return;
          Object.assign(values, body.coverage);
          setSnapshot({
            refreshKey: effectiveKey,
            ids: idsKey,
            values: { ...values },
            pending: offset + batchSize < requested.length,
          });
        }
      } catch {
        if (!controller.signal.aborted)
          setSnapshot({
            refreshKey: effectiveKey,
            ids: idsKey,
            values: { ...values },
            pending: false,
          });
      }
    })();
    return () => controller.abort();
  }, [path, parameter, idsKey, effectiveKey, batchSize, coalesceRefresh]);
  return snapshot !== null && snapshot.refreshKey === effectiveKey && snapshot.ids === idsKey
    ? snapshot
    : { values: {} as Record<string, T>, pending: Boolean(idsKey) };
}
