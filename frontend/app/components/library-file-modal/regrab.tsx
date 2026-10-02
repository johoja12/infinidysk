import { Badge } from "~/components/ui";

/** The Sonarr/Radarr media file a regrab acts on (`GET /api/arr-regrab`). */
export type RegrabTarget = {
  app: string;
  host: string;
  mediaKind: string;
  mediaIds: number[];
  fileId: number;
  label: string;
};

/** A persisted regrab request. */
export type RegrabRequestView = {
  id: string;
  status: string;
  source: string;
  releaseName: string;
  libraryPath?: string | null;
  arrApp?: string | null;
  arrHost?: string | null;
  arrMediaKind?: string | null;
  reason?: string | null;
  message?: string | null;
  linkRemoved?: boolean;
  blocklisted?: boolean;
  attempts?: number;
  createdAt: string;
  updatedAt?: string;
  requestedAt?: string | null;
};

export type RegrabPreview = {
  eligible: boolean;
  disabledReason?: string | null;
  releaseName?: string | null;
  libraryPath?: string | null;
  oldLibraryLink?: boolean | null;
  target?: RegrabTarget | null;
  request?: RegrabRequestView | null;
};

export type RegrabState = {
  loading: boolean;
  preview: RegrabPreview | null;
  error: string | null;
  confirming: boolean;
  pending: boolean;
};

export const idleRegrabState: RegrabState = {
  loading: false,
  preview: null,
  error: null,
  confirming: false,
  pending: false,
};

const activeStatuses = new Set(["pending", "requested", "search-withheld"]);

/** True while a regrab is queued or waiting for Sonarr/Radarr to import a replacement. */
export function isActiveRegrab(status: string | null | undefined): boolean {
  return status != null && activeStatuses.has(status);
}

export function regrabStatusLabel(status: string): string {
  switch (status) {
    case "pending":
      return "Regrab queued";
    case "requested":
      return "Regrab requested";
    case "search-withheld":
      return "Regrab requested (search limited)";
    case "replaced":
      return "Replaced";
    case "skipped":
      return "Regrab skipped";
    default:
      return "Regrab failed";
  }
}

/** Library list badge for items with an active regrab. */
export function RegrabBadge({ status }: { status: string | null | undefined }) {
  if (!isActiveRegrab(status)) return null;
  return <Badge className="badge-warning badge-soft">{regrabStatusLabel(status!)}</Badge>;
}
