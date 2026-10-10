import { describe, expect, it } from "vitest";
import {
  decimalBytes,
  describeCoverage,
  failureReason,
  isRangeJob,
  mebibytes,
  type CoverageJob,
} from "./job-coverage";

const MiB = 1024 * 1024;
const backfill: CoverageJob = {
  trigger: "backfill",
  state: "completed",
  start: 100 * MiB,
  length: 4 * MiB,
  committedBytes: 50_300_000,
  fileSize: 6_900_000_000,
  isRangeJob: true,
  rangeBytes: 4 * MiB,
  rangeCachedBytes: 4 * MiB,
};

describe("Smart Prefetch job coverage", () => {
  it("reports a finished backfill by its own range, with whole-file coverage as secondary", () => {
    const summary = describeCoverage(backfill);
    expect(summary.primary).toBe("Cached the 4 MiB playback missed ✓");
    expect(summary.secondary).toBe("1% of the whole file is cached");
    expect(summary.percent).toBe(100);
    expect(summary.complete).toBe(true);
  });

  it("shows partial range progress for a running backfill", () => {
    const summary = describeCoverage({
      ...backfill,
      state: "running",
      length: 40 * MiB,
      rangeBytes: 40 * MiB,
      rangeCachedBytes: 12 * MiB,
    });
    expect(summary.primary).toBe("12 MiB of 40 MiB of the missed range cached");
    expect(summary.percent).toBe(30);
    expect(summary.complete).toBe(false);
  });

  it("does not present a queued backfill as a coverage failure", () => {
    const summary = describeCoverage({ ...backfill, state: "queued", rangeCachedBytes: 0 });
    expect(summary.primary).toBe("Waiting to cache the 4 MiB playback missed");
    expect(summary.primary).not.toContain("%");
  });

  it("names other range jobs as the requested range", () => {
    expect(
      describeCoverage({ ...backfill, trigger: "plex:abc:source:def:movie:123:minimum" }).primary,
    ).toBe("Cached the 4 MiB requested range ✓");
  });

  it("falls back when the current revision's range coverage is unknown", () => {
    const summary = describeCoverage({ ...backfill, rangeBytes: null, rangeCachedBytes: null });
    expect(summary.primary).toBe("4 MiB range · current coverage unavailable");
    expect(summary.percent).toBeNull();
    expect(summary.secondary).toBe("1% of the whole file is cached");
  });

  it("keeps whole-file coverage for whole-file jobs", () => {
    const whole: CoverageJob = {
      trigger: "manual",
      state: "completed",
      start: 0,
      length: 0,
      committedBytes: 2_000_000_000,
      fileSize: 2_000_000_000,
    };
    expect(isRangeJob(whole)).toBe(false);
    const summary = describeCoverage(whole);
    expect(summary.primary).toBe("2 GB of 2 GB cached file bytes");
    expect(summary.secondary).toBeNull();
    expect(summary.percent).toBe(100);
  });

  it("infers range jobs from older responses without the flag", () => {
    const legacy: CoverageJob = { ...backfill };
    delete legacy.isRangeJob;
    expect(isRangeJob(legacy)).toBe(true);
    expect(describeCoverage({ ...legacy, length: 0, rangeBytes: null }).primary).toBe(
      "6.3 GiB range · current coverage unavailable",
    );
  });

  it("formats sizes", () => {
    expect(mebibytes(512 * 1024)).toBe("512 KiB");
    expect(mebibytes(1536 * MiB)).toBe("1.5 GiB");
    expect(decimalBytes(null)).toBe("—");
    expect(decimalBytes(999)).toBe("999 B");
  });
});

describe("Smart Prefetch failure reasons", () => {
  it("shows a later repair for an earlier successful warming attempt", () => {
    expect(failureReason({ state: "completed", repairOutcome: { status: "replaced" } })).toEqual({
      title: "Repair follow-up",
      remedy: "Replaced",
      tone: "warning",
    });
  });
  it.each([
    ["requested", "Replacement requested"],
    ["replacement-warmed", "Replacement fully warmed"],
    ["replacement-unavailable", "Replacement file unavailable"],
    ["search-withheld", "Replacement search withheld"],
    ["failed", "Repair failed"],
    ["unconfirmed", "Repair outcome unconfirmed"],
  ])("uses live repair outcome %s instead of the saved pending remedy", (status, label) => {
    expect(
      failureReason({
        state: "failed",
        failureCode: "source-damaged",
        remedy: "repair-pending",
        repairOutcome: { status },
      })?.remedy,
    ).toBe(label);
  });
  it("explains a damaged release and its repair hand-off", () => {
    expect(
      failureReason({ state: "failed", failureCode: "source-damaged", remedy: "repair-queued" }),
    ).toEqual({ title: "Release damaged on Usenet", remedy: "Queued for repair", tone: "error" });
  });

  it("labels cache storage errors and deferrals", () => {
    expect(failureReason({ state: "failed", failureCode: "cache-storage" })?.title).toBe(
      "Cache storage error",
    );
    expect(failureReason({ state: "queued", failureCode: "source-unverified" })).toEqual({
      title: "Source bytes did not verify",
      remedy: null,
      tone: "warning",
    });
  });

  it("falls back for unknown codes and older failed rows", () => {
    expect(failureReason({ state: "failed", failureCode: "new-code" })?.title).toBe(
      "Warming failed",
    );
    expect(failureReason({ state: "failed", error: "Old failure" })?.title).toBe("Warming failed");
    expect(failureReason({ state: "completed" })).toBeNull();
    expect(
      failureReason({ state: "failed", failureCode: "source-damaged", remedy: "unknown" })?.remedy,
    ).toBeNull();
  });
});
