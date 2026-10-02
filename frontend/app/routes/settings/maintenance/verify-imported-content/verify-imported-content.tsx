import { useCallback, useEffect, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Input, InputGroup, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { withUrlBase } from "~/utils/url-base";

// Mirrors ImportedContentSweepState from GET /api/imported-content-sweep.
export type ImportedContentSweepState = {
  status: "idle" | "running" | "paused" | "completed" | "failed";
  category?: string | null;
  limit?: number | null;
  cursor?: string | null;
  checked: number;
  healthy: number;
  damaged: number;
  repairsQueued: number;
  unverifiable: number;
  skipped: number;
  checkedThisRun: number;
  startedAt?: string | null;
  updatedAt?: string | null;
  finishedAt?: string | null;
  message?: string | null;
  recentFindings: { davItemId: string; path: string; detail: string; foundAt: string }[];
};

const POLL_INTERVAL_MS = 3000;

export function describeSweepStatus(state: ImportedContentSweepState | null): string {
  if (!state || state.status === "idle") return "Not started.";
  const scope = state.category ? `category ${state.category}` : "all imported files";
  const counts =
    `${state.checked.toLocaleString()} checked · ${state.damaged.toLocaleString()} damaged` +
    ` · ${state.repairsQueued.toLocaleString()} repairs queued · ${state.skipped.toLocaleString()} skipped`;
  const label =
    state.status === "running"
      ? "Running"
      : state.status === "paused"
        ? "Paused"
        : state.status === "completed"
          ? "Completed"
          : "Failed";
  return `${label} (${scope}): ${counts}${state.message ? `\n${state.message}` : ""}`;
}

export function VerifyImportedContent() {
  const [state, setState] = useState<ImportedContentSweepState | null>(null);
  const [category, setCategory] = useState("migration-plex");
  const [limit, setLimit] = useState("");
  const [restart, setRestart] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const response = await fetch(withUrlBase("/api/imported-content-sweep"));
      if (!response.ok) throw new Error(`Request failed (${response.status})`);
      setState((await response.json()) as ImportedContentSweepState);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to load sweep progress.");
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const isRunning = state?.status === "running";
  useEffect(() => {
    if (!isRunning) return;
    const timer = window.setInterval(() => void load(), POLL_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [isRunning, load]);

  const post = useCallback(async (path: string, body?: unknown) => {
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(
        withUrlBase(path),
        body
          ? {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify(body),
            }
          : { method: "POST" },
      );
      const data = (await response.json().catch(() => ({}))) as
        ImportedContentSweepState | { error?: string };
      if (response.status === 409) {
        setState(data as ImportedContentSweepState);
        setError("The sweep is already running.");
        return;
      }
      if (!response.ok) {
        throw new Error(
          (data as { error?: string }).error || `Request failed (${response.status})`,
        );
      }
      setState(data as ImportedContentSweepState);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Request failed.");
    } finally {
      setBusy(false);
    }
  }, []);

  const parsedLimit = limit.trim() === "" ? null : Number.parseInt(limit, 10);
  const limitInvalid = parsedLimit !== null && (!Number.isFinite(parsedLimit) || parsedLimit <= 0);
  const canResume = state?.status === "paused" && (state.category ?? "") === category.trim();

  return (
    <div className="space-y-4">
      <p className="text-sm leading-relaxed text-base-content/70">
        Verify that imported library files still read back as their own upload. Each file has a few
        dozen articles sampled and their yEnc headers checked; files whose articles now belong to a
        different post are queued for repair (PAR2 first, otherwise a Sonarr/Radarr replacement).
        Runs one file at a time at background priority and resumes after a restart.
      </p>

      {error && (
        <Alert className="alert-soft text-sm" variant="danger">
          {error}
        </Alert>
      )}

      <div className="space-y-3 rounded-lg border border-base-content/10 bg-base-200/40 p-3">
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="space-y-1">
            <span className="block text-[11px] text-base-content/45">
              Category (blank for every imported file)
            </span>
            <Input
              className="w-full"
              value={category}
              disabled={isRunning}
              onChange={(e) => setCategory(e.target.value)}
              placeholder="all categories"
            />
          </label>
          <label className="space-y-1">
            <span className="block text-[11px] text-base-content/45">
              File limit per run (blank for no limit)
            </span>
            <InputGroup
              className="w-full"
              type="number"
              min={1}
              suffix="files"
              value={limit}
              disabled={isRunning}
              onChange={(e) => setLimit(e.target.value)}
            />
          </label>
        </div>
        <Toggle
          id="imported-content-sweep-restart"
          className="cursor-pointer gap-2 p-0"
          checked={restart}
          disabled={isRunning}
          onChange={(e) => setRestart(e.target.checked)}
          label={<span className="text-sm text-base-content">Start over from the beginning</span>}
        />
        <div className="flex flex-col gap-3 border-t border-base-content/10 pt-3 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex shrink-0 gap-2">
            <Button
              variant={isRunning || busy || limitInvalid ? "secondary" : "primary"}
              disabled={isRunning || busy || limitInvalid}
              onClick={() =>
                void post("/api/imported-content-sweep/start", {
                  category: category.trim() || null,
                  limit: parsedLimit,
                  restart,
                })
              }
            >
              <Icon
                name={isRunning ? "progress_activity" : "fact_check"}
                className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
              />
              {isRunning ? "Running..." : canResume && !restart ? "Resume" : "Start"}
            </Button>
            <Button
              disabled={!isRunning || busy}
              onClick={() => void post("/api/imported-content-sweep/stop")}
            >
              <Icon name="stop_circle" className="!text-[18px]" />
              Stop
            </Button>
          </div>
          <div
            aria-live="polite"
            className="min-w-0 whitespace-pre-line break-words font-mono text-xs text-base-content/70"
          >
            {describeSweepStatus(state)}
          </div>
        </div>
      </div>

      {state && state.recentFindings.length > 0 && (
        <div className="space-y-2">
          <h3 className="text-xs font-medium uppercase tracking-wide text-base-content/50">
            Damaged files found
          </h3>
          <ul className="space-y-1.5">
            {state.recentFindings.map((finding) => (
              <li
                key={`${finding.davItemId}-${finding.foundAt}`}
                className="rounded-lg bg-base-200/30 px-3 py-2 text-xs leading-relaxed"
              >
                <p className="break-all font-mono text-base-content/80">{finding.path}</p>
                <p className="mt-0.5 text-base-content/55">{finding.detail}</p>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
