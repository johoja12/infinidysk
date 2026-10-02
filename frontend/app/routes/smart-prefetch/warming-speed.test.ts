import { describe, expect, it } from "vitest";
import {
  formatDuration,
  formatSpeed,
  formatWarmingSpeed,
  medianWarmingSpeed,
  warmingBytesPerSecond,
} from "./warming-speed";

describe("warming speed", () => {
  it("formats average speed and active duration for a recorded job", () => {
    // 14.2 MB/s for 3m 05s.
    expect(formatWarmingSpeed({ activeMs: 185_000, warmedBytes: 14_200_000 * 185 })).toBe(
      "14.2 MB/s · 3m 05s",
    );
  });

  it("shows an em dash when timing was not recorded", () => {
    expect(formatWarmingSpeed({})).toBe("—");
    expect(formatWarmingSpeed({ activeMs: null, warmedBytes: null })).toBe("—");
    expect(formatWarmingSpeed({ activeMs: 0, warmedBytes: 0 })).toBe("—");
    expect(formatWarmingSpeed({ activeMs: 1_000, warmedBytes: null })).toBe("—");
  });

  it("says nothing was fetched for a job that only verified cached bytes", () => {
    expect(formatWarmingSpeed({ activeMs: 2_000, warmedBytes: 0 })).toBe("nothing fetched · 2s");
  });

  it("scales speed units and duration ranges", () => {
    expect(formatSpeed(512)).toBe("512.0 B/s");
    expect(formatSpeed(1_500)).toBe("1.5 KB/s");
    expect(formatSpeed(2_500_000_000)).toBe("2.5 GB/s");
    expect(formatDuration(400)).toBe("0s");
    expect(formatDuration(45_000)).toBe("45s");
    expect(formatDuration(3_725_000)).toBe("1h 02m");
  });

  it("computes the median over jobs with recorded timing only", () => {
    const jobs = [
      { activeMs: 1_000, warmedBytes: 10_000_000 },
      { activeMs: 1_000, warmedBytes: 30_000_000 },
      { activeMs: null, warmedBytes: null },
      { activeMs: 5_000, warmedBytes: 0 },
      { activeMs: 1_000, warmedBytes: 20_000_000 },
    ];
    expect(medianWarmingSpeed(jobs)).toBe(20_000_000);
    expect(medianWarmingSpeed(jobs.slice(0, 2))).toBe(20_000_000);
    expect(medianWarmingSpeed([{}])).toBeNull();
    expect(warmingBytesPerSecond({ activeMs: 500, warmedBytes: 1_000 })).toBe(2_000);
  });
});
