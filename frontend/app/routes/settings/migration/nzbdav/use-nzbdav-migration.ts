import { useCallback, useEffect, useState } from "react";

export type NzbDavConnectForm = {
  packagePath: string;
  maxQueueDepth: number;
  submitWorkers: number;
};

export const DEFAULT_NZBDAV_CONNECT_FORM: NzbDavConnectForm = {
  packagePath: "/config/migration-input/nzbdav-canary",
  maxQueueDepth: 5,
  submitWorkers: 1,
};

export type NzbDavConnection = {
  packageDigest: string;
  selectionCount: number;
  exclusionCount: number;
  releaseCount: number;
  categories: string[];
};

export type NzbDavCategory = { source: string; target: string; action: "migrate" | "exclude" };

export type NzbDavCorrelationRow = {
  libraryRelativePath: string;
  legacyDavItemId: string;
  expectedFileSize: number;
  extractionStatus: string;
  exclusionReason?: string | null;
  correlationStatus: string;
  infiniDyskDavItemId?: string | null;
  correlationEvidence: string;
};

export type NzbDavCorrelation = {
  packageDigest: string;
  selectedCount: number;
  exclusionCount: number;
  ambiguityCount: number;
  exactCount: number;
  rows: NzbDavCorrelationRow[];
};

export type NzbDavFullBatchStatus = {
  batchIndex: number;
  selectionCount: number;
  status: string;
  appliedCount: number;
  validatedCount: number;
};

export type NzbDavFullStatus = {
  recoveryStatus: string;
  sourceLinkCount: number;
  recoverableCount: number;
  coverage: number;
  batchCount: number;
  selectedCount: number;
  appliedCount: number;
  validatedCount: number;
  batches: NzbDavFullBatchStatus[];
};

async function apiJson<T>(
  url: string,
  init?: RequestInit,
  fetcher: typeof fetch = fetch,
): Promise<T> {
  const response = await fetcher(url, { cache: "no-store", ...init });
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const body = (await response.json()) as { error?: string };
      if (body.error) message = body.error;
    } catch {
      // Keep the status-based message for a non-JSON failure.
    }
    throw new Error(message);
  }
  return (await response.json()) as T;
}

export function requestNzbDavConnect(
  form: NzbDavConnectForm,
  fetcher: typeof fetch = fetch,
): Promise<NzbDavConnection> {
  return apiJson<NzbDavConnection>(
    "/api/migration/nzbdav/connect",
    {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify(form),
    },
    fetcher,
  );
}

export function requestNzbDavPlan(
  fetcher: typeof fetch = fetch,
): Promise<{ actionableCount: number }> {
  return apiJson("/api/migration/nzbdav/canary-plan", { method: "POST" }, fetcher);
}

export function requestNzbDavFullStatus(fetcher: typeof fetch = fetch): Promise<NzbDavFullStatus> {
  return apiJson("/api/migration/nzbdav/full/status", undefined, fetcher);
}

export function requestNzbDavReconcile(
  fetcher: typeof fetch = fetch,
): Promise<{ exactCount: number; selectedCount?: number }> {
  return apiJson("/api/migration/nzbdav/reconcile", { method: "POST" }, fetcher);
}

export function isRunConfirmationExact(
  connection: NzbDavConnection | null,
  digest: string,
  count: string,
): boolean {
  return (
    connection !== null &&
    digest === connection.packageDigest &&
    count === String(connection.selectionCount)
  );
}

export function canGenerateCanaryPlan(
  sessionStatus: string | undefined,
  correlation: Pick<
    NzbDavCorrelation,
    "selectedCount" | "exactCount" | "exclusionCount" | "ambiguityCount"
  > | null,
): boolean {
  return (
    sessionStatus === "complete" &&
    correlation !== null &&
    correlation.selectedCount === correlation.exactCount &&
    correlation.exclusionCount === 0 &&
    correlation.ambiguityCount === 0
  );
}

export function canReconcileNzbDav(
  sessionStatus: string | undefined,
  correlation: Pick<NzbDavCorrelation, "selectedCount" | "exactCount"> | null,
): boolean {
  return (
    sessionStatus === "complete" &&
    correlation !== null &&
    correlation.exactCount < correlation.selectedCount
  );
}

export function useNzbDavMigration() {
  const [form, setForm] = useState(DEFAULT_NZBDAV_CONNECT_FORM);
  const [connection, setConnection] = useState<NzbDavConnection | null>(null);
  const [sessionStatus, setSessionStatus] = useState<string>();
  const [categories, setCategories] = useState<NzbDavCategory[]>([]);
  const [correlation, setCorrelation] = useState<NzbDavCorrelation | null>(null);
  const [fullStatus, setFullStatus] = useState<NzbDavFullStatus | null>(null);
  const [digestConfirmation, setDigestConfirmation] = useState("");
  const [countConfirmation, setCountConfirmation] = useState("");
  const [planReady, setPlanReady] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const mutate = useCallback(async (name: string, action: () => Promise<void>) => {
    setBusy(name);
    setError(null);
    try {
      await action();
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : String(caught));
    } finally {
      setBusy(null);
    }
  }, []);

  const refreshStatus = useCallback(async () => {
    const status = await apiJson<{ sessionStatus: string }>("/api/migration/nzbdav/status");
    setSessionStatus(status.sessionStatus);
    try {
      setFullStatus(await requestNzbDavFullStatus());
    } catch {
      setFullStatus(null);
    }
  }, []);

  useEffect(() => {
    void refreshStatus().catch(() => undefined);
  }, [refreshStatus]);

  useEffect(() => {
    if (
      !sessionStatus ||
      !["scanning", "scan_cancelling", "running", "cancelling"].includes(sessionStatus)
    )
      return;
    const handle = window.setInterval(() => void refreshStatus().catch(() => undefined), 2000);
    return () => window.clearInterval(handle);
  }, [refreshStatus, sessionStatus]);

  const connect = () =>
    mutate("connect", async () => {
      const result = await requestNzbDavConnect(form);
      setConnection(result);
      setCategories(result.categories.map((source) => ({ source, target: "", action: "migrate" })));
      setDigestConfirmation("");
      setCountConfirmation("");
      setCorrelation(null);
      setPlanReady(false);
      setSessionStatus("connected");
    });

  const saveCategories = () =>
    mutate("categories", async () => {
      await apiJson("/api/migration/nzbdav/categories", {
        method: "PUT",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          mappings: categories.map((category) => ({
            altmountCategory: category.source,
            targetCategory: category.action === "exclude" ? null : category.target,
            action: category.action,
          })),
        }),
      });
      setSessionStatus("mapped");
    });

  const scan = () =>
    mutate("scan", async () => {
      await apiJson("/api/migration/nzbdav/scan", { method: "POST" });
      setSessionStatus("scanning");
    });

  const run = () =>
    mutate("run", async () => {
      if (!connection || !isRunConfirmationExact(connection, digestConfirmation, countConfirmation))
        throw new Error("Confirm the exact package digest and selection count before Run.");
      await apiJson("/api/migration/nzbdav/run", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({
          packageDigest: connection.packageDigest,
          selectionCount: connection.selectionCount,
        }),
      });
      setSessionStatus("running");
    });

  const loadCorrelation = () =>
    mutate("correlation", async () => {
      const result = await apiJson<NzbDavCorrelation>("/api/migration/nzbdav/correlation");
      setCorrelation(result);
    });

  const reconcile = () =>
    mutate("reconcile", async () => {
      await requestNzbDavReconcile();
      setCorrelation(await apiJson<NzbDavCorrelation>("/api/migration/nzbdav/correlation"));
      try {
        setFullStatus(await requestNzbDavFullStatus());
      } catch {
        setFullStatus(null);
      }
    });

  const generatePlan = () =>
    mutate("plan", async () => {
      await requestNzbDavPlan();
      setPlanReady(true);
    });

  const onCategoryChange = (source: string, change: Partial<NzbDavCategory>) =>
    setCategories((current) =>
      current.map((category) =>
        category.source === source ? { ...category, ...change } : category,
      ),
    );

  return {
    form,
    setForm,
    connection,
    sessionStatus,
    categories,
    onCategoryChange,
    correlation,
    fullStatus,
    digestConfirmation,
    setDigestConfirmation,
    countConfirmation,
    setCountConfirmation,
    planReady,
    busy,
    error,
    connect,
    saveCategories,
    scan,
    run,
    loadCorrelation,
    reconcile,
    generatePlan,
  };
}
