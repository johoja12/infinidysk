import { describe, expect, it } from "vitest";
import { describeSweepStatus, type ImportedContentSweepState } from "./verify-imported-content";

const base: ImportedContentSweepState = {
  status: "running",
  category: "migration-plex",
  checked: 1200,
  healthy: 1197,
  damaged: 3,
  repairsQueued: 3,
  unverifiable: 0,
  skipped: 40,
  checkedThisRun: 1200,
  recentFindings: [],
};

describe("describeSweepStatus", () => {
  it("reports not started before the first run", () => {
    expect(describeSweepStatus(null)).toBe("Not started.");
    expect(describeSweepStatus({ ...base, status: "idle" })).toBe("Not started.");
  });

  it("summarises progress, scope and the latest message", () => {
    expect(describeSweepStatus({ ...base, message: "Checked a.mkv." })).toBe(
      "Running (category migration-plex): 1,200 checked · 3 damaged · 3 repairs queued · 40 skipped\nChecked a.mkv.",
    );
  });

  it("describes an all-library paused sweep", () => {
    expect(describeSweepStatus({ ...base, status: "paused", category: null })).toContain(
      "Paused (all imported files)",
    );
  });
});
