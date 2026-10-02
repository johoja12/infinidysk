import { useCallback, useRef, useState } from "react";
import { useFetcher } from "react-router";
import type { LibraryCatalogItem, LibraryFileDetails } from "~/clients/backend-client.server";
import { MediaPreview } from "~/components/media-preview";
import { withUrlBase } from "~/utils/url-base";
import { LibraryFileModal, type LibraryModalFeedback } from "./file-modal";

export type LibraryQuality = "4k" | "1080p" | "720p" | "sd" | "unknown";

/** What a page that only knows a DavItem id can show before (or instead of) library details. */
export type DavItemFileFallback = {
  displayName: string;
  size: number | null;
  cachePercentage: number | null;
};

/** Response of the `/library-file` resource route. */
export type LibraryFileLookup = {
  details: LibraryFileDetails | null;
  previewUrl: string | null;
  libraryRoot: string | null;
  /** Set when the file has no Media Library record; library-only actions are disabled. */
  unavailableReason: string | null;
};

type Resolved = {
  quality: LibraryQuality;
  cachePercentage: number | null;
  previewUrl: string | null;
  libraryRoot: string | null;
  libraryUnavailable: string | null;
};

/** Mirrors the backend's LibraryBrowseService.QualityFromName. */
export function qualityFromName(name: string): LibraryQuality {
  if (/(?:^|[^a-z0-9])(?:2160p|4k|uhd)(?:[^a-z0-9]|$)/i.test(name)) return "4k";
  if (/(?:^|[^a-z0-9])1080[pi]?(?:[^a-z0-9]|$)/i.test(name)) return "1080p";
  if (/(?:^|[^a-z0-9])720[pi]?(?:[^a-z0-9]|$)/i.test(name)) return "720p";
  if (/(?:^|[^a-z0-9])(?:480[pi]?|576[pi]?|sd)(?:[^a-z0-9]|$)/i.test(name)) return "sd";
  return "unknown";
}

/** Builds the catalog view of an imported file from its details, using the catalog's health rules. */
export function catalogItemFromDetails(details: LibraryFileDetails): LibraryCatalogItem {
  return {
    kind: "internal",
    davItemId: details.davItemId,
    displayName: details.name,
    contentPath: details.contentPath,
    size: details.size ?? null,
    mappingCount: details.mappings.length,
    health:
      details.mappings.length === 0
        ? "unmapped"
        : details.mappings.every((mapping) => mapping.status === "valid")
          ? "healthy"
          : "attention",
    mappings: details.mappings,
  };
}

function fallbackItem(davItemId: string, fallback: DavItemFileFallback): LibraryCatalogItem {
  return {
    kind: "internal",
    davItemId,
    displayName: fallback.displayName,
    contentPath: null,
    size: fallback.size,
    mappingCount: 0,
    health: "not in library",
    mappings: [],
  };
}

/**
 * State and actions behind the media file modal. The Media Library opens it with a
 * catalog item; other pages (Smart Prefetch) open it with a DavItem id.
 */
export function useLibraryFileModal(options: { prewarmAction?: string } = {}) {
  const { prewarmAction } = options;
  const fetcher = useFetcher<{ status: boolean; error?: string }>();
  const [selected, setSelected] = useState<LibraryCatalogItem | null>(null);
  const [details, setDetails] = useState<LibraryFileDetails | null>(null);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsError, setDetailsError] = useState<string | null>(null);
  const [showPreview, setShowPreview] = useState(false);
  const [actionPending, setActionPending] = useState(false);
  const [feedback, setFeedback] = useState<LibraryModalFeedback>(null);
  const [resolved, setResolved] = useState<Resolved | null>(null);
  // Ignores a slow lookup that finishes after another file was opened.
  const request = useRef(0);

  const reset = useCallback(() => {
    request.current += 1;
    setDetails(null);
    setDetailsError(null);
    setShowPreview(false);
    setFeedback(null);
    setResolved(null);
    return request.current;
  }, []);

  const open = useCallback(
    (item: LibraryCatalogItem) => {
      const token = reset();
      setSelected(item);
      if (item.kind === "internal" && item.davItemId) {
        setDetailsLoading(true);
        void fetch(
          withUrlBase(
            `/api/get-library-file-details?davItemId=${encodeURIComponent(item.davItemId)}`,
          ),
        )
          .then(async (response) => {
            if (!response.ok) throw new Error("Could not load file details.");
            const body = (await response.json()) as LibraryFileDetails;
            if (request.current === token) setDetails(body);
          })
          .catch(() => {
            if (request.current === token) setDetailsError("Could not load file details.");
          })
          .finally(() => {
            if (request.current === token) setDetailsLoading(false);
          });
      }
    },
    [reset],
  );

  const openByDavItemId = useCallback(
    (davItemId: string, fallback: DavItemFileFallback) => {
      const token = reset();
      setSelected(fallbackItem(davItemId, fallback));
      setResolved({
        quality: qualityFromName(fallback.displayName),
        cachePercentage: fallback.cachePercentage,
        previewUrl: null,
        libraryRoot: null,
        libraryUnavailable: null,
      });
      setDetailsLoading(true);
      void fetch(withUrlBase(`/library-file?davItemId=${encodeURIComponent(davItemId)}`))
        .then(async (response) => {
          if (!response.ok) throw new Error("Could not load file details.");
          const body = (await response.json()) as LibraryFileLookup;
          if (request.current !== token) return;
          if (body.details) {
            const item = catalogItemFromDetails(body.details);
            setDetails(body.details);
            setSelected(item);
            setResolved((current) => ({
              quality: qualityFromName(item.displayName),
              cachePercentage: current?.cachePercentage ?? fallback.cachePercentage,
              previewUrl: body.previewUrl,
              libraryRoot: body.libraryRoot,
              libraryUnavailable: null,
            }));
          } else {
            setResolved((current) =>
              current
                ? {
                    ...current,
                    libraryUnavailable:
                      body.unavailableReason ?? "This file is not in the Media Library.",
                  }
                : current,
            );
          }
        })
        .catch(() => {
          if (request.current === token) setDetailsError("Could not load file details.");
        })
        .finally(() => {
          if (request.current === token) setDetailsLoading(false);
        });
    },
    [reset],
  );

  const close = useCallback(() => {
    reset();
    setSelected(null);
  }, [reset]);

  const runAction = useCallback(async (fn: () => Promise<{ ok: boolean; message: string }>) => {
    setActionPending(true);
    setFeedback(null);
    try {
      const result = await fn();
      setFeedback({ variant: result.ok ? "success" : "danger", message: result.message });
    } catch (error) {
      setFeedback({
        variant: "danger",
        message: error instanceof Error ? error.message : "Action failed.",
      });
    } finally {
      setActionPending(false);
    }
  }, []);

  const onRunHealthCheck = useCallback(() => {
    void runAction(async () => {
      const response = await fetch(withUrlBase("/api/trigger-health-check"), { method: "POST" });
      if (response.status === 409) {
        const body = (await response.json().catch(() => null)) as { error?: string } | null;
        return { ok: false, message: body?.error || "Background repairs are disabled." };
      }
      if (!response.ok) return { ok: false, message: "Could not start health checks." };
      return { ok: true, message: "Health checks queued." };
    });
  }, [runAction]);

  const onRequeue = useCallback(() => {
    const id = selected?.davItemId;
    if (!id) return;
    void runAction(async () => {
      const response = await fetch(
        withUrlBase(`/api/requeue-action-needed-health-checks?davItemId=${encodeURIComponent(id)}`),
        { method: "POST" },
      );
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { error?: string } | null;
        return { ok: false, message: body?.error || "Could not queue this file for re-check." };
      }
      const body = (await response.json()) as { requeuedCount?: number };
      const count = body.requeuedCount ?? 0;
      return {
        ok: true,
        message: count === 0 ? "No action-needed result to re-check." : "File queued for re-check.",
      };
    });
  }, [runAction, selected]);

  const onPrewarm = useCallback(() => {
    if (!selected?.davItemId) return;
    const form = new FormData();
    form.set("operation", "prewarm");
    form.set("davItemId", selected.davItemId);
    void fetcher.submit(
      form,
      prewarmAction ? { method: "post", action: prewarmAction } : { method: "post" },
    );
  }, [fetcher, prewarmAction, selected]);

  return {
    selected,
    details,
    detailsLoading,
    detailsError,
    showPreview,
    setShowPreview,
    feedback:
      fetcher.data?.status === true
        ? ({ variant: "success", message: "Prewarm requested." } as const)
        : feedback,
    actionState:
      actionPending || fetcher.state !== "idle" ? ("pending" as const) : ("idle" as const),
    /** Error from the last prewarm request, shown on the page as well as in the modal. */
    prewarmError: fetcher.data?.status === false ? (fetcher.data.error ?? null) : null,
    prewarmFailed: fetcher.data?.status === false,
    resolved,
    open,
    openByDavItemId,
    close,
    onRunHealthCheck,
    onRequeue,
    onPrewarm,
  };
}

export type LibraryFileModalController = ReturnType<typeof useLibraryFileModal>;

/**
 * Renders the media file modal (and its preview player) for a controller. Pages that
 * know more about the selected file (quality, cache, signed preview) pass it as props;
 * otherwise the values resolved by `openByDavItemId` are used.
 */
export function LibraryFileModalHost({
  modal,
  canPrewarm,
  libraryRoot,
  quality,
  cachePercentage,
  previewUrl,
}: {
  modal: LibraryFileModalController;
  canPrewarm: boolean;
  libraryRoot?: string | null;
  quality?: LibraryQuality;
  cachePercentage?: number | null;
  previewUrl?: string | null;
}) {
  const { selected, resolved } = modal;
  if (!selected) return null;
  const effectivePreviewUrl =
    previewUrl !== undefined ? previewUrl : (resolved?.previewUrl ?? null);
  return (
    <>
      <LibraryFileModal
        item={selected}
        libraryRoot={libraryRoot !== undefined ? libraryRoot : (resolved?.libraryRoot ?? null)}
        quality={quality ?? resolved?.quality ?? "unknown"}
        cachePercentage={
          cachePercentage !== undefined ? cachePercentage : (resolved?.cachePercentage ?? null)
        }
        details={modal.details}
        detailsLoading={modal.detailsLoading}
        detailsError={modal.detailsError}
        previewUrl={effectivePreviewUrl}
        canPrewarm={canPrewarm}
        actionState={modal.actionState}
        feedback={modal.feedback}
        libraryUnavailable={resolved?.libraryUnavailable ?? null}
        onClose={modal.close}
        onPreview={() => modal.setShowPreview(true)}
        onRunHealthCheck={modal.onRunHealthCheck}
        onRequeue={modal.onRequeue}
        onPrewarm={modal.onPrewarm}
      />
      {modal.showPreview && effectivePreviewUrl ? (
        <MediaPreview
          fileName={selected.displayName}
          filePath={selected.contentPath ?? selected.displayName}
          mimeType={mimeTypeFor(selected.displayName)}
          sizeBytes={selected.size ?? null}
          previewUrl={effectivePreviewUrl}
          onClose={() => modal.setShowPreview(false)}
        />
      ) : null}
    </>
  );
}

export function mimeTypeFor(name: string): string {
  const lower = name.toLowerCase();
  if (lower.endsWith(".mp4")) return "video/mp4";
  if (lower.endsWith(".mkv")) return "video/x-matroska";
  if (lower.endsWith(".avi")) return "video/x-msvideo";
  if (lower.endsWith(".mov")) return "video/quicktime";
  if (lower.endsWith(".webm")) return "video/webm";
  if (lower.endsWith(".mp3")) return "audio/mpeg";
  if (lower.endsWith(".flac")) return "audio/flac";
  if (lower.endsWith(".ogg") || lower.endsWith(".oga")) return "audio/ogg";
  if (lower.endsWith(".m4a")) return "audio/mp4";
  if (lower.endsWith(".wav")) return "audio/wav";
  return "application/octet-stream";
}
