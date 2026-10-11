import { describe, expect, it } from "vitest";
import { groupWarmingHistory } from "./history-groups";

const run = {
  id: "old",
  itemId: "file",
  generation: "v3",
  start: 0,
  length: 0,
  updated: 10,
  trigger: "plex",
  committedBytes: 100,
  state: "completed",
  sourceCount: 1,
  sources: [{ label: "Next episode · realtime", category: "plex-realtime-next" }],
};

describe("warming history groups", () => {
  it("combines triggers while keeping the latest measurements and original runs", () => {
    const latest = {
      ...run,
      id: "new",
      updated: 20,
      sources: [{ label: "Next episode · history", category: "plex-history-next" }],
    };
    const jobs = [run, latest];
    const group = groupWarmingHistory(jobs)[0]!;
    expect(group.job.id).toBe("new");
    expect(group.job.committedBytes).toBe(100);
    expect(group.job.sources).toHaveLength(2);
    expect(group.runs).toEqual([latest, run]);
    expect(jobs).toEqual([run, latest]);
    expect(run.sources).toHaveLength(1);
  });

  it("never combines different files, content generations or requested ranges", () => {
    const variants = [
      run,
      { ...run, itemId: "other" },
      { ...run, generation: "v4" },
      { ...run, start: 100 },
      { ...run, length: 100 },
    ];
    expect(groupWarmingHistory(variants)).toHaveLength(5);
  });

  it("orders entries by newest run regardless of input order", () => {
    const groups = groupWarmingHistory([
      run,
      { ...run, itemId: "other", updated: 30 },
      { ...run, id: "new", updated: 40 },
    ]);
    expect(groups.map((group) => group.job.itemId)).toEqual(["file", "other"]);
  });

  it("retains previous failures without changing the latest outcome", () => {
    const failed = { ...run, state: "failed", error: "Damaged source" };
    const group = groupWarmingHistory([failed, { ...run, id: "new", updated: 20 }])[0]!;
    expect(group.job.state).toBe("completed");
    expect(group.runs[1]).toBe(failed);
  });

  it("deduplicates matching sources and preserves a single run unchanged", () => {
    expect(groupWarmingHistory([run])[0]!.job).toBe(run);
    const group = groupWarmingHistory([run, { ...run, id: "new" }])[0]!;
    expect(group.job.sources).toHaveLength(1);
    expect(group.job.sourceCount).toBe(1);
  });
});
