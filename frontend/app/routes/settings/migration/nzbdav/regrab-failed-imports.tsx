import { useCallback, useEffect, useId, useState, type ChangeEvent } from "react";
import { Alert, Badge, Button, Icon, InputGroup } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

/** One row of a saved `GET /api/migration/nzbdav/import-failures` report. */
export type MigrationFailureRecord = {
  legacyDavItemId?: string | null;
  libraryRelativePath?: string | null;
  reason?: string | null;
  submissionState?: string | null;
  sourceReleaseId?: string | null;
  originalTarget?: string | null;
  releaseName?: string | null;
  batchIndex?: number | null;
};

type DryRun = {
  total: number;
  eligible: number;
  alreadyRequested: number;
  skipped: Record<string, number>;
  previewToken: string;
};

type ApiBody = {
  error?: string;
  message?: string;
  total?: number;
  eligible?: number;
  alreadyRequested?: number;
  skipped?: Record<string, number>;
  previewToken?: string;
  remaining?: number;
};

const skipLabels: Record<string, string> = {
  "not-damaged-or-missing-articles": "other failure reason",
  "not-a-confirmed-failure": "not a confirmed failure",
  "invalid-record": "invalid record",
  "source-link-missing": "old library link gone",
  "source-link-changed": "already replaced",
  "not-a-symlink": "not a symlink",
  "outside-library-roots": "outside library roots",
  "ambiguous-library-root": "ambiguous library root",
};

/**
 * Reads saved import-failure reports (one JSON file per batch) into failure records.
 * Each report's top-level `batchIndex` is copied onto its failures.
 */
export function parseFailureReports(texts: string[]): MigrationFailureRecord[] {
  const failures: MigrationFailureRecord[] = [];
  for (const text of texts) {
    const report = JSON.parse(text) as {
      batchIndex?: number | null;
      failures?: MigrationFailureRecord[];
    };
    if (!Array.isArray(report.failures))
      throw new Error("A report has no failures array. Use the saved import-failures.json files.");
    for (const failure of report.failures)
      failures.push({ ...failure, batchIndex: failure.batchIndex ?? report.batchIndex ?? null });
  }
  return failures;
}

/**
 * One-off, bounded "Regrab failed migration imports" for rows already recorded as
 * import-failed by earlier batches. A dry run is required before every bounded run.
 */
export function RegrabFailedImports() {
  const fileInputId = useId();
  const limitId = useId();
  const [failures, setFailures] = useState<MigrationFailureRecord[]>([]);
  const [dryRun, setDryRun] = useState<DryRun | null>(null);
  const [limit, setLimit] = useState("25");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [counts, setCounts] = useState<Record<string, number> | null>(null);

  const loadCounts = useCallback(async () => {
    try {
      const response = await fetch(withUrlBase("/api/arr-regrab/migration-failures"));
      if (!response.ok) return;
      const body = (await response.json()) as { statusCounts?: Record<string, number> };
      setCounts(body.statusCounts ?? {});
    } catch {
      // Counts are informational only.
    }
  }, []);

  useEffect(() => {
    void loadCounts();
  }, [loadCounts]);

  const onFiles = useCallback(async (event: ChangeEvent<HTMLInputElement>) => {
    setDryRun(null);
    setMessage(null);
    setError(null);
    try {
      const files = Array.from(event.target.files ?? []);
      const texts = await Promise.all(files.map((file) => file.text()));
      setFailures(parseFailureReports(texts));
    } catch (problem) {
      setFailures([]);
      setError(problem instanceof Error ? problem.message : "Could not read the reports.");
    }
  }, []);

  const post = useCallback(async (path: string, body: object): Promise<ApiBody> => {
    const response = await fetch(withUrlBase(path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const json = (await response.json().catch(() => null)) as ApiBody | null;
    if (!response.ok) throw new Error(json?.error || `Request failed (${response.status})`);
    return json ?? {};
  }, []);

  const runDryRun = useCallback(async () => {
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const body = await post("/api/arr-regrab/migration-failures/dry-run", { failures });
      setDryRun({
        total: body.total ?? 0,
        eligible: body.eligible ?? 0,
        alreadyRequested: body.alreadyRequested ?? 0,
        skipped: body.skipped ?? {},
        previewToken: body.previewToken ?? "",
      });
    } catch (problem) {
      setDryRun(null);
      setError(problem instanceof Error ? problem.message : "Dry run failed.");
    } finally {
      setBusy(false);
    }
  }, [failures, post]);

  const runRegrab = useCallback(async () => {
    if (!dryRun) return;
    setBusy(true);
    setError(null);
    try {
      const body = await post("/api/arr-regrab/migration-failures", {
        failures,
        limit: Number(limit),
        previewToken: dryRun.previewToken,
      });
      const remaining = body.remaining ?? 0;
      setMessage(
        `${body.message ?? "Queued."}${
          remaining > 0 ? ` ${remaining} more can be queued after another dry run.` : ""
        }`,
      );
      setDryRun(null);
      void loadCounts();
    } catch (problem) {
      setError(problem instanceof Error ? problem.message : "Regrab failed.");
    } finally {
      setBusy(false);
    }
  }, [dryRun, failures, limit, loadCounts, post]);

  const limitValue = Number(limit);
  const limitValid = Number.isInteger(limitValue) && limitValue >= 1 && limitValue <= 500;
  const toQueue = dryRun ? Math.min(dryRun.eligible, limitValid ? limitValue : 0) : 0;

  return (
    <section className="rounded-box border border-base-300 p-4 space-y-3">
      <div className="flex items-start gap-3">
        <span className="rounded-lg bg-warning/10 p-2 text-warning">
          <Icon name="autorenew" className="!text-[20px]" />
        </span>
        <div>
          <h3 className="font-semibold">Regrab failed migration imports</h3>
          <p className="mt-0.5 text-xs leading-relaxed text-base-content/60">
            For releases that earlier batches recorded as import-failed because they are damaged on
            Usenet or have missing articles. InfiniDysk removes only the old library symlink (never
            its target or legacy NzbDav data), then Sonarr/Radarr removes the orphaned file record
            and searches for a replacement. New batches do this automatically.
          </p>
        </div>
      </div>

      {counts && Object.keys(counts).length > 0 && (
        <div className="flex flex-wrap gap-2" aria-label="Migration regrab states">
          {Object.entries(counts).map(([status, count]) => (
            <Badge key={status} className="badge-soft">
              {status}: {count}
            </Badge>
          ))}
        </div>
      )}

      <div className="space-y-2">
        <label className="block text-sm font-medium" htmlFor={fileInputId}>
          Saved import-failures reports
        </label>
        <input
          id={fileInputId}
          type="file"
          accept="application/json,.json"
          multiple
          className="file-input file-input-sm w-full max-w-md"
          onChange={(event) => void onFiles(event)}
        />
        {failures.length > 0 && (
          <p className="text-xs text-base-content/60">{failures.length} failed imports loaded.</p>
        )}
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <Button
          variant="outline"
          disabled={busy || failures.length === 0}
          onClick={() => void runDryRun()}
        >
          <Icon name="science" className="!text-[18px]" />
          Dry run
        </Button>
        <div className="space-y-1">
          <label className="block text-xs font-medium" htmlFor={limitId}>
            Limit per run
          </label>
          <InputGroup
            id={limitId}
            className="w-36"
            type="number"
            min={1}
            max={500}
            suffix="items"
            value={limit}
            onChange={(event) => setLimit(event.target.value)}
          />
        </div>
        <Button
          variant="danger"
          disabled={busy || !dryRun || toQueue === 0}
          onClick={() => void runRegrab()}
        >
          <Icon name="autorenew" className="!text-[18px]" />
          {toQueue > 0 ? `Regrab ${toQueue}` : "Regrab"}
        </Button>
      </div>

      {dryRun && (
        <Alert variant="info" role="status">
          <div className="space-y-1 text-sm">
            <p>
              Dry run: <strong>{dryRun.eligible}</strong> of {dryRun.total} can be regrabbed;{" "}
              {dryRun.alreadyRequested} already have a regrab.
            </p>
            {Object.keys(dryRun.skipped).length > 0 && (
              <p className="text-xs opacity-80">
                Skipped:{" "}
                {Object.entries(dryRun.skipped)
                  .map(([reason, count]) => `${count} ${skipLabels[reason] ?? reason}`)
                  .join(", ")}
              </p>
            )}
          </div>
        </Alert>
      )}
      {message && (
        <Alert variant="success" role="status">
          {message}
        </Alert>
      )}
      {error && (
        <Alert variant="danger" role="alert">
          {error}
        </Alert>
      )}
      <p className="text-xs text-base-content/50">
        Requests are rate-limited, processed one at a time in the background, and resume after a
        restart. Sonarr/Radarr timeouts are retried automatically.
      </p>
    </section>
  );
}
