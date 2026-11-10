import type { Schemas } from "../api/client";
export type Job = Schemas["PrefetchJob"];
export type Row = {
  key: string;
  job: Job;
  at: number;
  runs: { job: Job; at: number }[];
};
export const terminal = (j: Job) => !["queued", "paused"].includes(j.state);
export const requested = (j: Job) =>
  j.ranges.reduce((n, r) => n + Math.max(0, r.end - r.start), 0);
export function source(j: Job) {
  if (j.family === "backfill") return "Playback gap fill";
  if (j.family === "read") return "Finish watched";
  if (!j.automatic_scope) return "Manual";
  return (
    j.attribution?.policy?.source_title ||
    j.attribution?.policy?.source.replaceAll("_", " ") ||
    "Plex policy"
  );
}
export function state(
  j: Job,
  rates: Schemas["PrefetchRate"][],
  paused: boolean,
  now: number,
) {
  if (j.state !== "queued") return j.state;
  if (paused) return "paused";
  if (rates.some((r) => r.job === j.id && r.run === j.run && r.active))
    return "running";
  return j.next_attempt > now ? "deferred" : "queued";
}
export function rows(
  data: Schemas["PrefetchView"],
  history: boolean,
  gaps: boolean,
  grouped: boolean,
): Row[] {
  if (!history)
    return data.jobs
      .filter((j) => !terminal(j))
      .map((job) => ({
        key: job.id,
        job,
        at: job.updated_at,
        runs: [{ job, at: job.updated_at }],
      }));
  const attempts = new Map<string, { job: Job; at: number }>();
  for (const h of data.history)
    attempts.set(`${h.job.id}:${h.job.run}`, { job: h.job, at: h.recorded_at });
  for (const j of data.jobs.filter(terminal)) {
    const key = `${j.id}:${j.run}`;
    if (!attempts.has(key)) attempts.set(key, { job: j, at: j.updated_at });
  }
  const result = new Map<string, Row>();
  for (const entry of [...attempts.values()].sort(
    (a, b) => b.at - a.at || a.job.id.localeCompare(b.job.id),
  )) {
    const j = entry.job;
    if ((j.family === "backfill") !== gaps) continue;
    const key =
      grouped && !gaps
        ? JSON.stringify([j.item, j.revision, j.ranges])
        : `${j.id}:${j.run}`;
    const existing = result.get(key);
    if (existing) existing.runs.push(entry);
    else result.set(key, { key, job: j, at: entry.at, runs: [entry] });
  }
  return [...result.values()];
}
export function viewers(j: Job) {
  const reported = (j.attribution?.playback ?? []).map(
    (p) => p.viewer_name || `Plex profile ${p.viewer}`,
  );
  return [
    ...new Set([
      ...(j.attribution?.policy
        ? [
            j.attribution.policy.viewer_name ||
              `Plex profile ${j.attribution.policy.viewer}`,
          ]
        : []),
      ...reported,
    ]),
  ];
}
export function rangeCoverage(
  job: Pick<Job, "ranges">,
  ranges: { start: number; end: number }[],
) {
  const normalized: { start: number; end: number }[] = [];
  for (const r of [...ranges].sort((a, b) => a.start - b.start)) {
    const last = normalized.at(-1);
    if (last && r.start <= last.end) last.end = Math.max(last.end, r.end);
    else normalized.push({ ...r });
  }
  return job.ranges.reduce(
    (n, r) =>
      n +
      normalized.reduce(
        (m, c) =>
          m + Math.max(0, Math.min(r.end, c.end) - Math.max(r.start, c.start)),
        0,
      ),
    0,
  );
}
