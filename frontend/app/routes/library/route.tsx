import type { Route } from "./+types/route";
import { useCallback, useState } from "react";
import { Form, Link, useFetcher, useNavigation, useSearchParams } from "react-router";
import {
  backendClient,
  type LibraryCatalogItem,
  type LibraryBrowseResponse,
  type LibraryFileDetails,
} from "~/clients/backend-client.server";
import { getDownloadKey } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../../server/runtime-config";
import { formatFileSize } from "~/utils/file-size";
import { withUrlBase } from "~/utils/url-base";
import { Alert, Badge, Button, Input, PageHeader } from "~/components/ui";
import { LibraryFileModal, type LibraryModalFeedback } from "./file-modal";
import { MediaPreview } from "~/components/media-preview";

const PAGE_SIZE_OPTIONS = [25, 50, 100] as const;
const DEFAULT_PAGE_SIZE = 25;

export type LibraryPageData = {
  query: { q: string; type: string; page: number; pageSize: number };
  catalog: LibraryBrowseResponse;
  downloadKeys: Record<string, string>;
  previewUrls: Record<string, string>;
  nativeCacheActive: boolean;
};

function parseType(value: string | null) {
  return value === "internal" || value === "external" || value === "broken" ? value : "all";
}

function parsePage(value: string | null): number {
  const page = parseInt(value ?? "1", 10);
  return Number.isFinite(page) && page > 0 ? page : 1;
}

function parsePageSize(value: string | null): number {
  const size = parseInt(value ?? String(DEFAULT_PAGE_SIZE), 10);
  return (PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : DEFAULT_PAGE_SIZE;
}

export async function loader({ request }: Route.LoaderArgs): Promise<LibraryPageData> {
  const url = new URL(request.url);
  const query = {
    q: url.searchParams.get("q")?.trim() ?? "",
    type: parseType(url.searchParams.get("type")),
    page: parsePage(url.searchParams.get("page")),
    pageSize: parsePageSize(url.searchParams.get("pageSize")),
  };
  const catalog = await backendClient.getLibraryBrowse({
    ...(query.q ? { q: query.q } : {}),
    type: query.type as "all" | "internal" | "external" | "broken",
    page: query.page,
    pageSize: query.pageSize,
  });
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  const downloadKeys: Record<string, string> = {};
  const previewUrls: Record<string, string> = {};
  for (const item of catalog.groups.flatMap((group) => group.files.map((file) => file.item))) {
    if (item.kind === "internal" && item.contentPath && item.davItemId) {
      const relative = item.contentPath.startsWith("/")
        ? item.contentPath.slice(1)
        : item.contentPath;
      const key = getDownloadKey(relative, frontendBackendApiKey);
      downloadKeys[item.davItemId] = key;
      previewUrls[item.davItemId] = `/view${item.contentPath}?downloadKey=${key}`;
    }
  }
  let nativeCacheActive: boolean;
  try {
    const status = await backendClient.getNativeCacheStatus();
    nativeCacheActive = status.activeMode === "native";
  } catch {
    nativeCacheActive = false;
  }
  return { query, catalog, downloadKeys, previewUrls, nativeCacheActive };
}

export async function action({ request }: Route.ActionArgs) {
  const form = await request.formData();
  const operation = form.get("operation");
  const davItemId = form.get("davItemId");
  if (operation === "prewarm" && typeof davItemId === "string" && davItemId) {
    try {
      await backendClient.warmPrefetch([davItemId]);
      return Response.json({ status: true });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Could not prewarm this file.";
      return Response.json({ status: false, error: message }, { status: 502 });
    }
  }
  return Response.json({ status: false, error: "Unknown library action." }, { status: 400 });
}

export default function Library({ loaderData }: Route.ComponentProps) {
  const [searchParams] = useSearchParams();
  const navigation = useNavigation();
  const fetcher = useFetcher<{ status: boolean; error?: string }>();
  const { query, catalog, downloadKeys, previewUrls, nativeCacheActive } = loaderData;
  const totalPages = Math.max(1, Math.ceil(catalog.totalGroups / catalog.pageSize));
  const [selected, setSelected] = useState<LibraryCatalogItem | null>(null);
  const [details, setDetails] = useState<LibraryFileDetails | null>(null);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsError, setDetailsError] = useState<string | null>(null);
  const [showPreview, setShowPreview] = useState(false);
  const [actionPending, setActionPending] = useState(false);
  const [feedback, setFeedback] = useState<LibraryModalFeedback>(null);

  const openModal = useCallback((item: LibraryCatalogItem) => {
    setSelected(item);
    setDetails(null);
    setDetailsError(null);
    setShowPreview(false);
    setFeedback(null);
    if (item.kind === "internal" && item.davItemId) {
      const id = item.davItemId;
      setDetailsLoading(true);
      void fetch(withUrlBase(`/api/get-library-file-details?davItemId=${encodeURIComponent(id)}`))
        .then(async (response) => {
          if (!response.ok) throw new Error("Could not load file details.");
          setDetails((await response.json()) as LibraryFileDetails);
        })
        .catch(() => {
          setDetailsError("Could not load file details.");
        })
        .finally(() => {
          setDetailsLoading(false);
        });
    }
  }, []);

  const closeModal = useCallback(() => {
    setSelected(null);
    setDetails(null);
    setDetailsError(null);
    setShowPreview(false);
    setFeedback(null);
  }, []);

  const runAction = useCallback(async (fn: () => Promise<{ ok: boolean; message: string }>) => {
    setActionPending(true);
    setFeedback(null);
    try {
      const result = await fn();
      setFeedback(result.ok ? { variant: "success", message: result.message } : null);
      if (!result.ok) setFeedback({ variant: "danger", message: result.message });
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
        return {
          ok: false,
          message: body?.error || "Could not queue action-needed items for re-check.",
        };
      }
      const body = (await response.json()) as { requeuedCount?: number };
      const count = body.requeuedCount ?? 0;
      return {
        ok: true,
        message:
          count === 0
            ? "No current action-needed items to re-check."
            : `Queued ${count.toLocaleString()} item${count === 1 ? "" : "s"} for re-check.`,
      };
    });
  }, [runAction, selected]);

  const onPrewarm = useCallback(() => {
    const id = selected?.davItemId;
    if (!id) return;
    const form = new FormData();
    form.set("operation", "prewarm");
    form.set("davItemId", id);
    void fetcher.submit(form, { method: "post" });
  }, [fetcher, selected]);

  const selectedPreviewUrl =
    selected?.davItemId != null ? (previewUrls[selected.davItemId] ?? null) : null;
  const selectedItem = selected;
  const previewItem = showPreview && selectedItem ? selectedItem : null;

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-6 px-4 py-8 md:px-6">
      <PageHeader
        title="Media Library"
        subtitle="Browse indexed shows, movies, and unmatched files."
      />
      {navigation.state !== "idle" ? <p role="status">Loading library groups…</p> : null}
      {catalog.indexWarning ? (
        <div className="alert alert-warning">
          <span>Library index may be stale: {catalog.indexWarning}</span>
        </div>
      ) : null}
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <SummaryCard label="Shows" value={catalog.showCount} />
        <SummaryCard label="Movies" value={catalog.movieCount} />
        <SummaryCard label="Unmatched groups" value={catalog.unmatchedCount} />
        <SummaryCard label="Files in results" value={catalog.totalFiles} />
      </div>
      <div className="text-sm text-base-content/60">
        {catalog.indexScannedAt
          ? `Library index scanned ${new Date(catalog.indexScannedAt).toLocaleString()}`
          : "Library index has not completed a scan yet; results may be incomplete."}
      </div>
      <Form method="get" className="flex flex-wrap gap-2">
        <Input name="q" defaultValue={query.q} placeholder="Search title, content path, symlink…" />
        <select
          name="type"
          defaultValue={query.type}
          className="select select-bordered"
          aria-label="Mapping filter"
        >
          <option value="all">All mappings</option>
          <option value="internal">Internal</option>
          <option value="external">External</option>
          <option value="broken">Broken</option>
        </select>
        <select
          name="pageSize"
          defaultValue={query.pageSize}
          className="select select-bordered"
          aria-label="Groups per page"
        >
          {PAGE_SIZE_OPTIONS.map((size) => (
            <option key={size} value={size}>
              {size} groups
            </option>
          ))}
        </select>
        <Button type="submit">Search</Button>
      </Form>
      <p className="text-sm text-base-content/60">
        {catalog.totalGroups} groups · {catalog.totalFiles} files · page {catalog.page} of{" "}
        {totalPages}
      </p>
      {catalog.groups.length === 0 ? (
        <div className="rounded-xl border border-base-300 p-8 text-center text-base-content/60">
          {query.q || query.type !== "all"
            ? "No files match this search and filter."
            : "No indexed media yet. Check the Library Directory in Settings and wait for its first scan."}
        </div>
      ) : (
        catalog.groups.map((group) => (
          <details
            key={group.key}
            className="group rounded-xl border border-base-300 bg-base-200/40"
            open={catalog.groups.length === 1}
          >
            <summary className="flex cursor-pointer list-none flex-wrap items-center gap-3 p-4 marker:hidden md:p-5">
              <span className="material-symbols-outlined text-primary" aria-hidden="true">
                {group.kind === "show" ? "tv" : group.kind === "movie" ? "movie" : "folder_off"}
              </span>
              <span className="min-w-0 flex-1 truncate text-base font-semibold">{group.name}</span>
              <Badge>
                {group.kind === "show" ? "TV show" : group.kind === "movie" ? "Movie" : "Unmatched"}
              </Badge>
              <span className="text-xs text-base-content/60">
                {group.fileCount} files · {group.mappingCount} mappings ·{" "}
                {formatFileSize(group.totalSize)}
              </span>
              {group.attentionCount > 0 ? (
                <Badge>{group.attentionCount} need attention</Badge>
              ) : null}
              <span
                className="material-symbols-outlined transition-transform group-open:rotate-180"
                aria-hidden="true"
              >
                expand_more
              </span>
            </summary>
            <div className="border-t border-base-300 p-3 md:p-4">
              <div className="overflow-x-auto">
                <table className="table table-sm">
                  <thead>
                    <tr>
                      <th>File</th>
                      <th>Size</th>
                      <th>Mappings</th>
                      <th>Health</th>
                    </tr>
                  </thead>
                  <tbody>
                    {group.files.map(({ item, episodeLabel }) => (
                      <CatalogRow
                        key={item.davItemId ?? item.displayName}
                        item={item}
                        episodeLabel={episodeLabel}
                        downloadKeys={downloadKeys}
                        search={searchParams.toString()}
                        onOpen={() => openModal(item)}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </details>
        ))
      )}
      <nav className="join" aria-label="Pagination">
        {query.page > 1 ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page - 1)}`}>
            Previous
          </Link>
        ) : null}
        {query.page < totalPages ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page + 1)}`}>
            Next
          </Link>
        ) : null}
      </nav>
      {fetcher.data && fetcher.data.status === false ? (
        <Alert variant="danger" role="alert">
          {fetcher.data.error ?? "Could not prewarm this file."}
        </Alert>
      ) : null}
      {fetcher.data && fetcher.data.status === true && selected ? (
        <span className="sr-only" role="status">
          Prewarm requested.
        </span>
      ) : null}
      {selectedItem ? (
        <LibraryFileModal
          item={selectedItem}
          details={details}
          detailsLoading={detailsLoading}
          detailsError={detailsError}
          previewUrl={selectedPreviewUrl}
          canPrewarm={nativeCacheActive}
          actionState={actionPending || fetcher.state !== "idle" ? "pending" : "idle"}
          feedback={
            fetcher.data && fetcher.data.status === true
              ? { variant: "success", message: "Prewarm requested." }
              : feedback
          }
          onClose={closeModal}
          onPreview={() => setShowPreview(true)}
          onRunHealthCheck={onRunHealthCheck}
          onRequeue={onRequeue}
          onPrewarm={onPrewarm}
        />
      ) : null}
      {previewItem && selectedPreviewUrl ? (
        <MediaPreview
          fileName={previewItem.displayName}
          filePath={previewItem.contentPath ?? previewItem.displayName}
          mimeType={mimeTypeFor(previewItem.displayName)}
          sizeBytes={previewItem.size ?? null}
          previewUrl={selectedPreviewUrl}
          onClose={() => setShowPreview(false)}
        />
      ) : null}
    </div>
  );
}

function mimeTypeFor(name: string): string {
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

function withPage(params: URLSearchParams, page: number): string {
  const next = new URLSearchParams(params);
  next.set("page", String(page));
  return next.toString();
}

function SummaryCard({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-xl border border-base-300 bg-base-200/50 p-4">
      <div className="text-2xl font-semibold tabular-nums">{value.toLocaleString()}</div>
      <div className="text-xs text-base-content/60">{label}</div>
    </div>
  );
}

function CatalogRow({
  item,
  episodeLabel,
  downloadKeys,
  search,
  onOpen,
}: {
  item: LibraryCatalogItem;
  episodeLabel: string | null;
  downloadKeys: Record<string, string>;
  search: string;
  onOpen: () => void;
}) {
  return (
    <>
      <tr>
        <td>
          <button type="button" className="link text-left" aria-haspopup="dialog" onClick={onOpen}>
            {episodeLabel ? (
              <span className="mr-2 font-mono text-xs text-base-content/60">{episodeLabel}</span>
            ) : null}
            {item.displayName}
          </button>
        </td>
        <td className="max-w-xs truncate">
          {item.contentPath ?? item.mappings[0]?.targetText ?? "—"}
        </td>
        <td>{item.size != null ? formatFileSize(item.size) : "—"}</td>
        <td>{item.mappingCount}</td>
        <td>
          <Badge>{item.health}</Badge>
        </td>
      </tr>
      <tr>
        <td colSpan={4}>
          <details>
            <summary>{item.mappingCount} mapping(s) — expand to inspect</summary>
            <ul className="mt-2 flex flex-col gap-1">
              {item.mappings.map((m) => (
                <li key={m.linkPath} className="flex flex-wrap items-center gap-2 text-sm">
                  <Badge>{m.mappingType}</Badge>
                  <Badge>{m.status}</Badge>
                  <code className="break-all">
                    {m.linkPath} → {m.targetText}
                  </code>
                  {m.mappingType === "internal" && item.davItemId && item.contentPath ? (
                    <Link
                      className="link"
                      to={`/view${item.contentPath}?downloadKey=${downloadKeys[item.davItemId] ?? ""}&${search}`}
                    >
                      Open
                    </Link>
                  ) : (
                    <span className="text-base-content/50">inspection only</span>
                  )}
                </li>
              ))}
            </ul>
          </details>
        </td>
      </tr>
    </>
  );
}
