import { describe, expect, it } from "vitest";
import {
  formatDuration,
  formatLiveWarming,
  remainingBytes,
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

describe("live warming", () => {
  const running = {
    activeMs: 200_000,
    warmedBytes: 14_200_000 * 200,
    recentBytesPerSecond: 18_400_000,
    committedBytes: 3_000_000_000,
    fileSize: 7_800_000_000,
    lastProgressAt: 1_000_000,
    stalled: false,
  };

  it("shows current speed, average and an ETA from the current speed", () => {
    // 4.8 GB left at 18.4 MB/s is about 4m 21s.
    expect(formatLiveWarming(running, 1_000_000)).toBe(
      "18.4 MB/s now · 14.2 MB/s avg · ETA 4m 21s",
    );
  });

  it("falls back to the average for the ETA when nothing was fetched recently", () => {
    expect(formatLiveWarming({ ...running, recentBytesPerSecond: 0 }, 1_000_000)).toBe(
      "0.0 B/s now · 14.2 MB/s avg · ETA 5m 38s",
    );
  });

  it("flags a stalled job with how long it has been quiet", () => {
    expect(formatLiveWarming({ ...running, stalled: true }, 1_000_000 + 90_000)).toBe(
      "Stalled · no progress for 1m 30s",
    );
    expect(formatLiveWarming({ stalled: true })).toBe("Stalled");
  });

  it("measures before the first report and uses the job's own range for range jobs", () => {
    expect(formatLiveWarming({})).toBe("Measuring speed…");
    expect(
      remainingBytes({ isRangeJob: true, rangeBytes: 40, rangeCachedBytes: 12, fileSize: 1000 }),
    ).toBe(28);
    expect(remainingBytes({ isRangeJob: true, rangeBytes: null })).toBeNull();
    expect(remainingBytes({ fileSize: 100, committedBytes: 120 })).toBe(0);
  });
});
