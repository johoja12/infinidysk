import { Alert, Badge, Button, Icon, Modal } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import type { LibraryCatalogItem, LibraryFileDetails } from "~/clients/backend-client.server";

export type LibraryModalActionState = "idle" | "pending";

export type LibraryModalFeedback = {
  variant: "success" | "danger";
  message: string;
} | null;

export type LibraryFileModalProps = {
  item: LibraryCatalogItem;
  details: LibraryFileDetails | null;
  detailsLoading: boolean;
  detailsError: string | null;
  previewUrl: string | null;
  canPrewarm: boolean;
  actionState: LibraryModalActionState;
  feedback: LibraryModalFeedback;
  onClose: () => void;
  onPreview: () => void;
  onRunHealthCheck: () => void;
  onRequeue: () => void;
  onPrewarm: () => void;
};

export function LibraryFileModal(props: LibraryFileModalProps) {
  const { item, details, detailsLoading, detailsError, previewUrl, canPrewarm } = props;
  const latest = details?.latestHealth ?? null;
  const downloadUrl = previewUrl
    ? `${previewUrl}${previewUrl.includes("?") ? "&" : "?"}download=true`
    : null;

  return (
    <Modal open title={item.displayName} onClose={props.onClose} size="wide">
      <div className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2">
          <Badge>{item.health}</Badge>
          {item.size != null && (
            <span className="font-mono text-xs">{formatFileSize(item.size)}</span>
          )}
          <span className="break-all font-mono text-xs text-base-content/60">
            {item.contentPath ?? item.mappings[0]?.targetText ?? "—"}
          </span>
        </div>

        {detailsLoading && <p role="status">Loading file details…</p>}
        {detailsError && (
          <Alert variant="danger" role="alert">
            {detailsError}
          </Alert>
        )}

        <div className="flex flex-wrap gap-2">
          {previewUrl && (
            <Button
              size="small"
              onClick={props.onPreview}
              disabled={props.actionState === "pending"}
            >
              <Icon name="play_arrow" className="!text-[16px]" />
              Preview
            </Button>
          )}
          {downloadUrl && (
            <a className="btn btn-sm gap-2" href={downloadUrl}>
              <Icon name="download" className="!text-[16px]" />
              Download
            </a>
          )}
          {item.kind === "internal" && item.davItemId && (
            <>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRunHealthCheck}
                disabled={props.actionState === "pending"}
              >
                <Icon name="favorite" className="!text-[16px]" />
                Run health check
              </Button>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRequeue}
                disabled={props.actionState === "pending"}
              >
                <Icon name="refresh" className="!text-[16px]" />
                Requeue repair
              </Button>
              {canPrewarm ? (
                <Button
                  variant="outline"
                  size="small"
                  onClick={props.onPrewarm}
                  disabled={props.actionState === "pending"}
                >
                  <Icon name="bolt" className="!text-[16px]" />
                  Prewarm
                </Button>
              ) : null}
            </>
          )}
        </div>
        {!canPrewarm && item.kind === "internal" && (
          <p className="text-xs text-base-content/60">
            Prewarm is unavailable: Native cache is inactive.
          </p>
        )}
        <p className="text-xs text-base-content/60">
          Run health check queues all due checks, not just this file.
        </p>

        {props.feedback && (
          <Alert
            variant={props.feedback.variant}
            role={props.feedback.variant === "danger" ? "alert" : "status"}
          >
            {props.feedback.message}
          </Alert>
        )}

        <div>
          <h3 className="text-sm font-semibold">Mappings ({item.mappingCount})</h3>
          <ul className="mt-1 flex flex-col gap-1">
            {item.mappings.map((m) => (
              <li key={m.linkPath} className="flex flex-wrap items-center gap-2 text-sm">
                <Badge>{m.mappingType}</Badge>
                <Badge>{m.status}</Badge>
                <code className="break-all">
                  {m.linkPath} → {m.targetText}
                </code>
              </li>
            ))}
          </ul>
        </div>

        <div>
          <h3 className="text-sm font-semibold">Health history</h3>
          {latest ? (
            <p className="text-sm">
              {latest.result} · {latest.repairStatus} ·{" "}
              {new Date(latest.createdAt).toLocaleString()}
              {latest.message ? ` — ${latest.message}` : ""}
            </p>
          ) : (
            <p className="text-sm text-base-content/60">No health checks recorded for this file.</p>
          )}
          <a className="link text-sm" href="/health">
            Open Health
          </a>
        </div>
      </div>
    </Modal>
  );
}
