import { describe, expect, it } from "vitest";
import { groupPredictions, cacheState, type Prediction } from "./predictions";
const prediction: Prediction = {
  itemId: "file",
  displayName: "episode.mkv",
  showTitle: "Voyager",
  source: "Plex history prediction",
  reason: "Head",
  fileSize: 100,
  eligible: true,
  episodeTitle: "Learning Curve",
  season: 1,
  episode: 16,
  viewer: "Sam",
  attribution: { label: "Next episode · history · Sam", category: "plex-history-next" },
};
describe("prediction results", () => {
  it("groups ranges and multiple viewers without losing attribution, excluding other policies", () => {
    const groups = groupPredictions([
      prediction,
      { ...prediction, reason: "Tail" },
      {
        ...prediction,
        viewer: "Alice",
        attribution: { label: "Next episode · realtime · Alice", category: "plex-realtime-next" },
      },
      { ...prediction, attribution: { label: "Hub", category: "plex-source" } },
    ]);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      displayName: "Voyager",
      episodeCode: "S01E16",
      viewers: ["Sam", "Alice"],
      reasons: ["Head", "Tail"],
    });
    expect(groups[0]!.sources).toHaveLength(2);
  });
  it("keeps unmapped predictions distinct and never treats them as fully cached", () => {
    const groups = groupPredictions([
      { ...prediction, itemId: "00000000-0000-0000-0000-000000000000", plexRatingKey: "1" },
      { ...prediction, itemId: "00000000-0000-0000-0000-000000000000", plexRatingKey: "2" },
    ]);
    expect(groups).toHaveLength(2);
    expect(cacheState(groups[0]!, null, [])).toBe("missing");
  });
  it("uses current cache coverage after eviction, even when historical warming completed", () => {
    const group = groupPredictions([prediction])[0]!;
    const cache = {
      available: true as const,
      length: 100,
      cachedBytes: 20,
      ranges: [{ offset: 0, count: 20 }],
      complete: true,
    };
    expect(cacheState(group, cache, [{ itemId: "file", state: "completed" }])).toBe("partial");
    expect(cacheState(group, { ...cache, cachedBytes: 0 }, [])).toBe("queued");
    expect(cacheState(group, cache, [{ itemId: "file", state: "running" }])).toBe("warming");
    expect(cacheState(group, { ...cache, cachedBytes: 100 }, [])).toBe("ready");
  });
});

it("separates unknown coverage from a missing mapping, loading and failed requests", () => {
  const group = groupPredictions([prediction])[0]!;
  expect(cacheState(group, null, [])).toBe("unavailable");
  expect(cacheState(group, null, [], { loading: true, error: null })).toBe("loading");
  expect(cacheState(group, null, [], { loading: false, error: "Failed" })).toBe("error");
  expect(cacheState({ ...group, itemId: null }, null, [], { loading: true, error: null })).toBe(
    "missing",
  );
});

it("keeps each viewer's watched-state warning when predictions share a file", () => {
  const group = groupPredictions([
    { ...prediction, watchedStatus: "verified", serverName: "Plex" },
    {
      ...prediction,
      viewer: "Alice",
      watchedStatus: "unconnected",
      watchedWarning: "Connect this profile",
      serverName: "Plex",
    },
  ])[0]!;
  expect(group.watchStates).toEqual([
    { viewer: "Sam", status: "verified", server: "Plex", warning: undefined },
    { viewer: "Alice", status: "unconnected", server: "Plex", warning: "Connect this profile" },
  ]);
});
