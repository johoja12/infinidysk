import type { ActiveRead } from "~/clients/backend-client.server";

/** How long a playback row stays after its last read ends, waiting for the next burst. */
export const BURST_GRACE_MS = 30_000;

type Counters = { bytesRead: number; bytesFetched: number; providers: Map<string, number> };

type Playback = {
  rowId: string;
  startedAt: number;
  /** Totals of reads that already ended. */
  done: Counters;
  /** Latest counters of reads still open, by read id. */
  open: Map<string, Counters>;
  last: ActiveRead;
  endedAt: number | null;
};

/** Playbacks by client and file; carried between websocket ticks. */
export type PlaybackState = Map<string, Playback>;

export type PlaybackRow = { read: ActiveRead; idle: boolean };

/**
 * Players such as rclone read a file in bursts of separate range requests, each a short
 * active-read session. One client reading one file is one playback, whatever its bursts.
 */
export function playbackKey(read: ActiveRead): string {
  return `${read.path}\u0000${read.clientIp ?? ""}\u0000${read.clientUserAgent ?? ""}`;
}

function countersOf(read: ActiveRead): Counters {
  return {
    bytesRead: read.bytesRead,
    bytesFetched: read.bytesFetched ?? 0,
    providers: new Map(read.providers.map((p) => [p.host, p.segments])),
  };
}

function addInto(total: Counters, part: Counters) {
  total.bytesRead += part.bytesRead;
  total.bytesFetched += part.bytesFetched;
  for (const [host, segments] of part.providers)
    total.providers.set(host, (total.providers.get(host) ?? 0) + segments);
}

/**
 * Folds one tick of active reads into playbacks: one row per playback, with cumulative bytes
 * and provider segments across its bursts. A playback without an open read stays for
 * {@link BURST_GRACE_MS} as an idle row, then is dropped.
 */
export function mergeReadBursts(
  state: PlaybackState,
  reads: ActiveRead[],
  now: number,
): { state: PlaybackState; rows: PlaybackRow[] } {
  const byKey = new Map<string, ActiveRead[]>();
  for (const read of reads) {
    const key = playbackKey(read);
    byKey.set(key, [...(byKey.get(key) ?? []), read]);
  }

  const next: PlaybackState = new Map();
  for (const [key, playback] of state) {
    const current = byKey.get(key) ?? [];
    const currentIds = new Set(current.map((read) => read.id));
    const done = { ...playback.done, providers: new Map(playback.done.providers) };
    for (const [id, counters] of playback.open) if (!currentIds.has(id)) addInto(done, counters);
    const endedAt = current.length > 0 ? null : (playback.endedAt ?? now);
    if (endedAt !== null && now - endedAt > BURST_GRACE_MS) continue;
    next.set(key, { ...playback, done, open: new Map(), endedAt });
  }

  for (const [key, current] of byKey) {
    const ordered = [...current].sort((a, b) => a.startedAt - b.startedAt);
    const existing = next.get(key);
    const playback: Playback = existing ?? {
      rowId: ordered[0].id,
      startedAt: ordered[0].startedAt,
      done: { bytesRead: 0, bytesFetched: 0, providers: new Map() },
      open: new Map(),
      last: ordered[0],
      endedAt: null,
    };
    for (const read of ordered) playback.open.set(read.id, countersOf(read));
    playback.last = ordered.reduce((latest, read) =>
      read.lastActivityAt >= latest.lastActivityAt ? read : latest,
    );
    playback.startedAt = Math.min(playback.startedAt, ordered[0].startedAt);
    playback.endedAt = null;
    next.set(key, playback);
  }

  const rows: PlaybackRow[] = [];
  for (const playback of next.values()) {
    const total = { ...playback.done, providers: new Map(playback.done.providers) };
    for (const counters of playback.open.values()) addInto(total, counters);
    const names = new Map(playback.last.providers.map((p) => [p.host, p.nickname]));
    rows.push({
      idle: playback.open.size === 0,
      read: {
        ...playback.last,
        id: playback.rowId,
        startedAt: playback.startedAt,
        bytesRead: total.bytesRead,
        bytesFetched: total.bytesFetched,
        providers: [...total.providers]
          .map(([host, segments]) => ({ host, nickname: names.get(host), segments }))
          .sort((a, b) => b.segments - a.segments),
      },
    });
  }
  return { state: next, rows };
}
