import type { FileCacheMap } from "~/components/library-file-modal/use-file-cache";
import type { JobSource } from "./source-bubbles";
export type Prediction = {
  itemId: string;
  displayName: string;
  source: string;
  reason: string;
  fileSize: number;
  eligible: boolean;
  plexRatingKey?: string;
  showTitle?: string;
  episodeTitle?: string | undefined;
  season?: number;
  episode?: number;
  viewer?: string;
  watchedStatus?: string;
  watchedWarning?: string;
  serverName?: string;
  attribution?: JobSource;
};
export type PredictionJob = { itemId: string; state: string };
export type PredictionGroup = {
  key: string;
  itemId: string | null;
  displayName: string;
  fileSize: number;
  episodeTitle?: string | undefined;
  episodeCode: string;
  viewers: string[];
  sources: JobSource[];
  reasons: string[];
  watchStates: {
    viewer: string;
    server?: string | undefined;
    status: string;
    warning?: string | undefined;
  }[];
};
const empty = "00000000-0000-0000-0000-000000000000";
export function groupPredictions(predictions: Prediction[]): PredictionGroup[] {
  const groups = new Map<string, PredictionGroup>();
  for (const prediction of predictions) {
    if (
      !prediction.attribution ||
      !["plex-history-next", "plex-realtime-next"].includes(prediction.attribution.category)
    )
      continue;
    const itemId = prediction.itemId && prediction.itemId !== empty ? prediction.itemId : null;
    const key =
      itemId ?? `${prediction.plexRatingKey}:${prediction.displayName}:${prediction.viewer}`;
    let group = groups.get(key);
    if (!group) {
      group = {
        key,
        itemId,
        displayName: prediction.showTitle || prediction.displayName,
        fileSize: prediction.fileSize,
        episodeTitle: prediction.episodeTitle,
        episodeCode:
          prediction.season != null && prediction.episode != null
            ? `S${String(prediction.season).padStart(2, "0")}E${String(prediction.episode).padStart(2, "0")}`
            : "",
        viewers: [],
        sources: [],
        reasons: [],
        watchStates: [],
      };
      groups.set(key, group);
    }
    const viewer = prediction.viewer || "Unknown user";
    if (!group.viewers.includes(viewer)) group.viewers.push(viewer);
    if (
      prediction.watchedStatus &&
      !group.watchStates.some(
        (state) =>
          state.viewer === viewer &&
          state.server === prediction.serverName &&
          state.status === prediction.watchedStatus,
      )
    )
      group.watchStates.push({
        viewer,
        server: prediction.serverName,
        status: prediction.watchedStatus,
        warning: prediction.watchedWarning,
      });
    if (
      !group.sources.some(
        (source) =>
          source.label === prediction.attribution!.label &&
          source.category === prediction.attribution!.category,
      )
    )
      group.sources.push(prediction.attribution);
    if (!group.reasons.includes(prediction.reason)) group.reasons.push(prediction.reason);
  }
  return [...groups.values()];
}
export function cacheState(
  group: PredictionGroup,
  cache: FileCacheMap | null,
  jobs: PredictionJob[],
  query: { loading: boolean; error: string | null } = { loading: false, error: null },
): "ready" | "warming" | "queued" | "partial" | "unavailable" | "missing" | "loading" | "error" {
  if (!group.itemId) return "missing";
  if (query.error) return "error";
  if (!cache && query.loading) return "loading";
  if (!cache) return "unavailable";
  if (cache.cachedBytes >= cache.length) return "ready";
  if (jobs.some((job) => job.itemId === group.itemId && job.state === "running")) return "warming";
  return cache.cachedBytes > 0 ? "partial" : "queued";
}
