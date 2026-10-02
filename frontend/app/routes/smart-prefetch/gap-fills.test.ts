import { describe, expect, it } from "vitest";
import { clock, gapHeadline, gapSpan, isGapFill, missReason, viewerLabel } from "./gap-fills";

const MiB = 1024 * 1024;
const gap = {
  trigger: "backfill",
  state: "completed",
  start: 1_000_000_000,
  length: 4 * MiB,
  fileSize: 4_000_000_000,
  rangeBytes: 4 * MiB,
};

describe("playback gap fills", () => {
  it("identifies backfill jobs only", () => {
    expect(isGapFill(gap)).toBe(true);
    expect(isGapFill({ trigger: "manual" })).toBe(false);
  });

  it("places the gap in time when Plex reported the runtime", () => {
    expect(gapHeadline({ ...gap, mediaDurationMs: 52 * 60_000 })).toBe(
      "Playback streamed 4 MiB at ~13:00 of 52:00 directly from Usenet",
    );
  });

  it("falls back to the position in the file without a runtime", () => {
    expect(gapHeadline(gap)).toBe(
      "Playback streamed 4 MiB at 25% into the file directly from Usenet",
    );
    expect(gapHeadline({ ...gap, fileSize: null, rangeBytes: null, length: 0 })).toBe(
      "Playback streamed a range directly from Usenet",
    );
  });

  it("maps recorded reasons and labels older rows honestly", () => {
    expect(missReason({ missReason: "buffers-full" }).label).toBe("Cache buffers full");
    expect(missReason({ missReason: null }).label).toBe("Reason not recorded");
    expect(missReason({ missReason: "future-code" }).label).toBe("Reason not recorded");
  });

  it("formats clocks, spans and viewers", () => {
    expect(clock(4_382_000)).toBe("1:13:02");
    expect(clock(65_000)).toBe("1:05");
    expect(gapSpan(gap)).toEqual({ at: 0.25, width: (4 * MiB) / 4_000_000_000 });
    expect(viewerLabel({ ...gap, viewerPlayer: "Living Room TV", viewerUser: "alex" })).toBe(
      "Living Room TV · alex",
    );
    expect(viewerLabel(gap)).toBeNull();
  });
});
