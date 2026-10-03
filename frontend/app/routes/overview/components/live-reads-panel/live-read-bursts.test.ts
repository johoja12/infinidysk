import { describe, expect, it } from "vitest";
import type { ActiveRead } from "~/clients/backend-client.server";
import { BURST_GRACE_MS, mergeReadBursts, type PlaybackState } from "./live-read-bursts";

function read(id: string, overrides: Partial<ActiveRead> = {}): ActiveRead {
  return {
    id,
    fileName: "Coyote.vs.Acme.mkv",
    path: "/content/movies/Coyote.vs.Acme.mkv",
    startedAt: 1_000,
    lastActivityAt: 1_000,
    bytesRead: 0,
    bytesFetched: 0,
    currentOffset: 0,
    fileSize: 19_000_000_000,
    clientIp: "192.168.20.80",
    clientUserAgent: "rclone/v1.72.0",
    providers: [],
    ...overrides,
  };
}

describe("mergeReadBursts", () => {
  it("keeps one row across a player's bursts and accumulates served bytes", () => {
    let state: PlaybackState = new Map();
    let result = mergeReadBursts(state, [read("a", { bytesRead: 80, currentOffset: 100 })], 1_000);
    state = result.state;
    expect(result.rows).toHaveLength(1);
    expect(result.rows[0]).toMatchObject({ idle: false, read: { id: "a", bytesRead: 80 } });

    // The burst ends: the row stays, idle, with the bytes it served.
    result = mergeReadBursts(state, [], 5_000);
    state = result.state;
    expect(result.rows[0]).toMatchObject({ idle: true, read: { id: "a", bytesRead: 80 } });

    // The next burst continues the same row.
    result = mergeReadBursts(
      state,
      [read("b", { startedAt: 8_000, lastActivityAt: 9_000, bytesRead: 90, currentOffset: 190 })],
      9_000,
    );
    expect(result.rows).toHaveLength(1);
    expect(result.rows[0]).toMatchObject({
      idle: false,
      read: { id: "a", startedAt: 1_000, bytesRead: 170, currentOffset: 190 },
    });
  });

  it("drops an idle row once the grace period passes", () => {
    let { state } = mergeReadBursts(new Map(), [read("a", { bytesRead: 10 })], 1_000);
    ({ state } = mergeReadBursts(state, [], 2_000));
    expect(mergeReadBursts(state, [], 2_000 + BURST_GRACE_MS).rows).toHaveLength(1);
    expect(mergeReadBursts(state, [], 2_001 + BURST_GRACE_MS).rows).toHaveLength(0);
  });

  it("keeps different clients and files apart", () => {
    const { rows } = mergeReadBursts(
      new Map(),
      [
        read("a"),
        read("b", { clientIp: "192.168.20.81" }),
        read("c", { path: "/content/movies/Other.mkv" }),
      ],
      1_000,
    );
    expect(rows.map((row) => row.read.id).sort()).toEqual(["a", "b", "c"]);
  });

  it("merges overlapping reads of one playback and sums Usenet fetches and segments", () => {
    const { state } = mergeReadBursts(
      new Map(),
      [
        read("a", {
          bytesFetched: 40,
          providers: [{ host: "news.a", nickname: "A", segments: 3 }],
        }),
      ],
      1_000,
    );
    const { rows } = mergeReadBursts(
      state,
      [
        read("a", {
          bytesFetched: 50,
          providers: [{ host: "news.a", nickname: "A", segments: 4 }],
        }),
        read("b", {
          startedAt: 2_000,
          lastActivityAt: 2_500,
          bytesFetched: 20,
          providers: [{ host: "news.a", nickname: "A", segments: 2 }],
        }),
      ],
      2_500,
    );
    expect(rows).toHaveLength(1);
    expect(rows[0].read).toMatchObject({
      id: "a",
      bytesFetched: 70,
      providers: [{ host: "news.a", nickname: "A", segments: 6 }],
    });
  });
});
