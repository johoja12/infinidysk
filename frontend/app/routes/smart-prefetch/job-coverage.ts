/** Fields of a Smart Prefetch job that describe what it covers and how it ended. */
export type CoverageJob = {
  trigger: string;
  state: string;
  start: number;
  length: number;
  committedBytes: number;
  fileSize?: number | null;
  isRangeJob?: boolean;
  rangeBytes?: number | null;
  rangeCachedBytes?: number | null;
};

export type CoverageSummary = {
  /** Main coverage figure: own-range coverage for range jobs, whole-file coverage otherwise. */
  primary: string;
  /** Whole-file coverage for range jobs; null for whole-file jobs. */
  secondary: string | null;
  /** Progress bar percentage of the primary figure, or null when unknown. */
  percent: number | null;
  /** Accessible label for the progress bar. */
  label: string;
  complete: boolean;
};

const BACKFILL = "backfill";
const MiB = 1024 * 1024;
const binary = new Intl.NumberFormat("en-US", { maximumFractionDigits: 1 });
const decimal = new Intl.NumberFormat("en-US", { maximumFractionDigits: 1 });

/** Binary size for block-aligned ranges, e.g. `4 MiB`, `1.5 GiB`. */
export function mebibytes(value: number): string {
  if (value >= 1024 * MiB) return `${binary.format(value / (1024 * MiB))} GiB`;
  if (value >= MiB) return `${binary.format(value / MiB)} MiB`;
  return `${binary.format(value / 1024)} KiB`;
}

/** Decimal size for whole files, matching the rest of the page, e.g. `6.9 GB`. */
export function decimalBytes(value?: number | null): string {
  if (value == null || !Number.isFinite(value)) return "—";
  if (value < 1000) return `${decimal.format(value)} B`;
  const unit = Math.min(4, Math.floor(Math.log(value) / Math.log(1000)));
  return `${decimal.format(value / 1000 ** unit)} ${["B", "KB", "MB", "GB", "TB"][unit]}`;
}

export function isRangeJob(job: CoverageJob): boolean {
  return job.isRangeJob ?? (job.start !== 0 || job.length !== 0);
}

function wholeFilePercent(job: CoverageJob): number | null {
  return job.fileSize && job.fileSize > 0
    ? Math.min(100, Math.max(0, Math.round((job.committedBytes / job.fileSize) * 100)))
    : null;
}

function requestedLength(job: CoverageJob): number | null {
  if (job.length > 0) return job.length;
  return job.fileSize && job.fileSize > job.start ? job.fileSize - job.start : null;
}

/**
 * What a job's coverage means. A range job (backfill, minimum head/tail, resume range) is judged
 * by the bytes of its own range, so a finished 4 MiB backfill of a 7 GB file reads as done rather
 * than as "1% cached"; whole-file coverage stays available as secondary text.
 */
export function describeCoverage(job: CoverageJob): CoverageSummary {
  const filePercent = wholeFilePercent(job);
  if (!isRangeJob(job)) {
    return {
      primary: `${decimalBytes(job.committedBytes)}${job.fileSize ? ` of ${decimalBytes(job.fileSize)}` : ""} cached file bytes`,
      secondary: null,
      percent: filePercent,
      label: `${filePercent ?? 0}% whole-file cache coverage`,
      complete: filePercent === 100,
    };
  }
  const backfill = job.trigger === BACKFILL;
  const what = backfill ? "playback missed" : "requested range";
  const secondary = filePercent === null ? null : `${filePercent}% of the whole file is cached`;
  const bytes = job.rangeBytes ?? null;
  const cached = job.rangeCachedBytes ?? null;
  if (bytes === null || cached === null || bytes <= 0) {
    const length = requestedLength(job);
    return {
      primary: `${length === null ? "Range" : `${mebibytes(length)} range`} · current coverage unavailable`,
      secondary,
      percent: null,
      label: "Range coverage unavailable",
      complete: false,
    };
  }
  const percent = Math.min(100, Math.max(0, Math.floor((cached / bytes) * 100)));
  if (cached >= bytes) {
    return {
      primary: `Cached the ${mebibytes(bytes)} ${backfill ? "playback missed" : "requested range"} ✓`,
      secondary,
      percent: 100,
      label: `${mebibytes(bytes)} range fully cached`,
      complete: true,
    };
  }
  if (cached === 0 && (job.state === "queued" || job.state === "paused")) {
    return {
      primary: `Waiting to cache the ${mebibytes(bytes)} ${what}`,
      secondary,
      percent: 0,
      label: `${mebibytes(bytes)} range not cached yet`,
      complete: false,
    };
  }
  return {
    primary: `${mebibytes(cached)} of ${mebibytes(bytes)} of the ${backfill ? "missed range" : "requested range"} cached`,
    secondary,
    percent,
    label: `${percent}% of the job's range cached`,
    complete: false,
  };
}

export type FailureReason = {
  title: string;
  /** Short follow-up badge, e.g. "Queued for repair"; null when none. */
  remedy: string | null;
  tone: "error" | "warning";
};

const reasons: Record<string, { title: string; tone: FailureReason["tone"] }> = {
  "source-damaged": { title: "Release damaged on Usenet", tone: "error" },
  "source-unverified": { title: "Source bytes did not verify", tone: "warning" },
  "source-unavailable": { title: "Source unavailable", tone: "warning" },
  "source-layout": { title: "Source layout problem", tone: "error" },
  "source-or-cache-io": { title: "Source or cache I/O error", tone: "warning" },
  "cache-storage": { title: "Cache storage error", tone: "error" },
  "storage-access": { title: "Cache storage access denied", tone: "error" },
  "source-changed": { title: "Source changed", tone: "warning" },
  budget: { title: "Daily budget reached", tone: "warning" },
  busy: { title: "Waiting for playback to finish", tone: "warning" },
  "invalid-request": { title: "Invalid warming request", tone: "error" },
  "unexpected-cancellation": { title: "Warming interrupted", tone: "warning" },
  "unexpected-failure": { title: "Warming failed", tone: "error" },
};

const remedies: Record<string, string> = {
  "repair-queued": "Queued for repair",
  "repair-pending": "Repair pending",
  "repair-disabled": "Repair disabled",
  "repair-unavailable": "Repair unavailable",
};

/** Headline for a failed or deferred job, from its stable failure code. */
export function failureReason(job: {
  state: string;
  failureCode?: string | null;
  remedy?: string | null;
  error?: string | null;
}): FailureReason | null {
  if (!job.failureCode) {
    return job.state === "failed" && job.error
      ? { title: "Warming failed", remedy: null, tone: "error" }
      : null;
  }
  const known = reasons[job.failureCode] ?? { title: "Warming failed", tone: "error" as const };
  return {
    title: known.title,
    remedy: job.remedy ? (remedies[job.remedy] ?? null) : null,
    tone: job.state === "failed" ? "error" : known.tone,
  };
}
