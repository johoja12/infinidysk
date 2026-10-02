import { mebibytes } from "./job-coverage";

/** Fields of a backfill ("playback gap fill") job that describe the gap and why it happened. */
export type GapFillJob = {
  trigger: string;
  state: string;
  start: number;
  length: number;
  fileSize?: number | null;
  rangeBytes?: number | null;
  missReason?: string | null;
  viewerUser?: string | null;
  viewerPlayer?: string | null;
  mediaDurationMs?: number | null;
};

export type MissReason = {
  label: string;
  /** One sentence for the tooltip: what happened during playback. */
  help: string;
  tone: "warning" | "error" | "info";
};

const BACKFILL = "backfill";

/** Codes recorded by the backend (`BackfillMissReasons`), in the order the filter lists them. */
export const missReasons: Record<string, MissReason> = {
  "buffers-full": {
    label: "Cache buffers full",
    help: "Every cache buffer was busy, so playback read these bytes straight from Usenet.",
    tone: "warning",
  },
  "block-busy": {
    label: "Block already filling",
    help: "Another reader was caching the same block, so playback did not wait for it.",
    tone: "info",
  },
  "storage-slow": {
    label: "Cache storage too slow",
    help: "A cache read or write missed its deadline, so playback skipped the cache.",
    tone: "warning",
  },
  "write-queue-full": {
    label: "Cache write queue full",
    help: "Too many cache writes were pending, so these bytes were not saved.",
    tone: "warning",
  },
  "write-failed": {
    label: "Cache write failed",
    help: "Writing these bytes to cache storage failed or was rejected.",
    tone: "error",
  },
  "source-interrupted": {
    label: "Source interrupted",
    help: "Usenet fetching hiccuped, so caching paused for the rest of that stream.",
    tone: "warning",
  },
  "source-changed": {
    label: "Source changed",
    help: "The file was replaced during playback, so the old bytes could not be cached.",
    tone: "info",
  },
  "uncached-read": {
    label: "Read without caching",
    help: "Playback read these bytes directly from Usenet without saving them.",
    tone: "info",
  },
};

const unrecorded: MissReason = {
  label: "Reason not recorded",
  help: "This gap fill was recorded before miss reasons were tracked.",
  tone: "info",
};

export function isGapFill(job: { trigger: string }): boolean {
  return job.trigger === BACKFILL;
}

export function missReason(job: { missReason?: string | null }): MissReason {
  return (job.missReason && missReasons[job.missReason]) || unrecorded;
}

/** `21:25` or `1:13:02`. */
export function clock(milliseconds: number): string {
  const total = Math.max(0, Math.round(milliseconds / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = String(total % 60).padStart(2, "0");
  return hours > 0
    ? `${hours}:${String(minutes).padStart(2, "0")}:${seconds}`
    : `${minutes}:${seconds}`;
}

/** Start and length of the gap as fractions of the file, or null without a known file size. */
export function gapSpan(job: GapFillJob): { at: number; width: number } | null {
  if (!job.fileSize || job.fileSize <= 0) return null;
  const length = job.length > 0 ? job.length : job.fileSize - job.start;
  const at = Math.min(1, Math.max(0, job.start / job.fileSize));
  return { at, width: Math.min(1 - at, Math.max(0, length / job.fileSize)) };
}

/**
 * Where in the media the gap was: a timestamp when Plex reported the runtime (estimated from the
 * byte offset, so approximate for variable bitrate), otherwise the position in the file.
 */
export function gapPosition(job: GapFillJob): string | null {
  const span = gapSpan(job);
  if (!span) return null;
  if (job.mediaDurationMs && job.mediaDurationMs > 0)
    return `~${clock(span.at * job.mediaDurationMs)} of ${clock(job.mediaDurationMs)}`;
  return `${Math.round(span.at * 100)}% into the file`;
}

export function gapBytes(job: GapFillJob): number | null {
  if (job.rangeBytes != null && job.rangeBytes > 0) return job.rangeBytes;
  return job.length > 0 ? job.length : null;
}

/** `Playback streamed 4 MiB at ~21:25 of 52:00 directly from Usenet` */
export function gapHeadline(job: GapFillJob): string {
  const size = gapBytes(job);
  const position = gapPosition(job);
  return `Playback streamed ${size === null ? "a range" : mebibytes(size)}${position ? ` at ${position}` : ""} directly from Usenet`;
}

/** `Living Room TV · alex`, or null when no Plex session was matched. */
export function viewerLabel(job: GapFillJob): string | null {
  const parts = [job.viewerPlayer, job.viewerUser].filter((part): part is string => Boolean(part));
  return parts.length ? parts.join(" · ") : null;
}
