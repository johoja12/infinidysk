/** Timing fields a Smart Prefetch job reports once it has run (null for older jobs). */
export type WarmingTiming = {
  activeMs?: number | null;
  warmedBytes?: number | null;
};

const rate = new Intl.NumberFormat("en-US", { maximumFractionDigits: 1, minimumFractionDigits: 1 });

/** Average bytes per second a job fetched while running, or null when it was not recorded. */
export function warmingBytesPerSecond(job: WarmingTiming): number | null {
  if (job.activeMs == null || job.warmedBytes == null) return null;
  if (!Number.isFinite(job.activeMs) || !Number.isFinite(job.warmedBytes)) return null;
  if (job.activeMs <= 0 || job.warmedBytes < 0) return null;
  return (job.warmedBytes / job.activeMs) * 1000;
}

/** Decimal transfer rate, e.g. `14.2 MB/s`. */
export function formatSpeed(bytesPerSecond: number): string {
  const units = ["B/s", "KB/s", "MB/s", "GB/s"];
  let value = Math.max(0, bytesPerSecond);
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit += 1;
  }
  return `${rate.format(value)} ${units[unit]}`;
}

/** Compact duration, e.g. `45s`, `3m 05s`, `1h 02m`. */
export function formatDuration(milliseconds: number): string {
  const seconds = Math.max(0, Math.round(milliseconds / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${String(seconds % 60).padStart(2, "0")}s`;
  return `${Math.floor(minutes / 60)}h ${String(minutes % 60).padStart(2, "0")}m`;
}

/** `14.2 MB/s · 3m 05s` for a job with recorded timing, otherwise an em dash. */
export function formatWarmingSpeed(job: WarmingTiming): string {
  const speed = warmingBytesPerSecond(job);
  if (speed === null || job.activeMs == null) return "—";
  // A job that only re-verified cached blocks fetched nothing; a 0 B/s speed would read as a stall.
  if (job.warmedBytes === 0) return `nothing fetched · ${formatDuration(job.activeMs)}`;
  return `${formatSpeed(speed)} · ${formatDuration(job.activeMs)}`;
}

/**
 * Median average speed across jobs that fetched bytes, or null when none did. Jobs that
 * only re-verified cached data are left out so they do not read as slow providers.
 */
export function medianWarmingSpeed(jobs: readonly WarmingTiming[]): number | null {
  const speeds = jobs
    .map(warmingBytesPerSecond)
    .filter((speed): speed is number => speed !== null && speed > 0)
    .sort((left, right) => left - right);
  if (speeds.length === 0) return null;
  const middle = Math.floor(speeds.length / 2);
  return speeds.length % 2 === 1 ? speeds[middle]! : (speeds[middle - 1]! + speeds[middle]!) / 2;
}

/** Live fields a running job reports, plus what is needed to estimate the time left. */
export type LiveWarming = WarmingTiming & {
  recentBytesPerSecond?: number | null;
  lastProgressAt?: number | null;
  stalled?: boolean | null;
  committedBytes?: number;
  fileSize?: number | null;
  isRangeJob?: boolean;
  rangeBytes?: number | null;
  rangeCachedBytes?: number | null;
};

/** Bytes the job still has to cache: its own range for range jobs, otherwise the rest of the file. */
export function remainingBytes(job: LiveWarming): number | null {
  if (job.isRangeJob) {
    if (job.rangeBytes == null || job.rangeCachedBytes == null) return null;
    return Math.max(0, job.rangeBytes - job.rangeCachedBytes);
  }
  if (!job.fileSize || job.committedBytes == null) return null;
  return Math.max(0, job.fileSize - job.committedBytes);
}

/**
 * Live status of a running job, e.g. `18.4 MB/s now · 14.2 MB/s avg · ETA 4m 10s`, or
 * `Stalled · no progress for 1m 30s`. The current speed covers the last ~20 seconds; the ETA
 * uses it (or the average when nothing was fetched recently).
 */
export function formatLiveWarming(job: LiveWarming, now: number = Date.now()): string {
  if (job.stalled) {
    const since = job.lastProgressAt == null ? null : Math.max(0, now - job.lastProgressAt);
    return since === null ? "Stalled" : `Stalled · no progress for ${formatDuration(since)}`;
  }
  const average = warmingBytesPerSecond(job);
  const recent =
    job.recentBytesPerSecond != null && Number.isFinite(job.recentBytesPerSecond)
      ? Math.max(0, job.recentBytesPerSecond)
      : null;
  const parts: string[] = [];
  if (recent !== null) parts.push(`${formatSpeed(recent)} now`);
  if (average !== null && average > 0) parts.push(`${formatSpeed(average)} avg`);
  const rate = recent !== null && recent > 0 ? recent : average;
  const remaining = remainingBytes(job);
  if (rate !== null && rate > 0 && remaining !== null && remaining > 0)
    parts.push(`ETA ${formatDuration((remaining / rate) * 1000)}`);
  return parts.length > 0 ? parts.join(" · ") : "Measuring speed…";
}
