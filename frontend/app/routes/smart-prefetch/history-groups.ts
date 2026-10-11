import type { JobSource } from "./source-bubbles";

type HistoryRun = {
  id: string;
  itemId: string;
  generation?: string | null;
  start: number;
  length: number;
  updated: number;
  sources?: JobSource[] | null;
  sourceCount?: number | null;
  source?: string;
  trigger: string;
};

export function warmingHistoryKey(job: HistoryRun): string {
  return JSON.stringify([job.itemId, job.generation ?? null, job.start, job.length]);
}

/** Keep each run intact; the newest supplies coverage, outcome and speed. */
export function groupWarmingHistory<T extends HistoryRun>(jobs: T[]) {
  const groups = new Map<string, T[]>();
  for (const job of jobs) {
    const key = warmingHistoryKey(job);
    const runs = groups.get(key) ?? [];
    runs.push(job);
    groups.set(key, runs);
  }
  return [...groups.entries()]
    .map(([key, values]) => {
      const runs = [...values].sort((a, b) => b.updated - a.updated || a.id.localeCompare(b.id));
      const latest = runs[0]!;
      const sources = new Map<string, JobSource>();
      for (const run of runs) {
        for (const source of run.sources?.length
          ? run.sources
          : [{ label: run.source || run.trigger, category: "other" }]) {
          sources.set(JSON.stringify([source.category, source.label]), source);
        }
      }
      const job: T =
        runs.length === 1
          ? latest
          : {
              ...latest,
              sources: [...sources.values()],
              sourceCount: Math.max(sources.size, ...runs.map((run) => run.sourceCount ?? 0)),
            };
      return { key, job, runs };
    })
    .sort((a, b) => b.job.updated - a.job.updated || a.key.localeCompare(b.key));
}
