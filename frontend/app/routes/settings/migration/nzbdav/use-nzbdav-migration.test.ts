import { describe, expect, it, vi } from "vitest";
import {
  DEFAULT_NZBDAV_CONNECT_FORM,
  canReconcileNzbDav,
  canGenerateCanaryPlan,
  isRunConfirmationExact,
  requestNzbDavConnect,
  requestNzbDavFullStatus,
  requestNzbDavPlan,
  requestNzbDavReconcile,
} from "./use-nzbdav-migration";

describe("NzbDav migration requests", () => {
  it("validates the package with one worker and queue depth five by default", async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(
        JSON.stringify({
          packageDigest: "a".repeat(64),
          selectionCount: 24,
          exclusionCount: 2,
          releaseCount: 8,
          categories: ["Migration-TV"],
        }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );

    const result = await requestNzbDavConnect(DEFAULT_NZBDAV_CONNECT_FORM, fetcher);

    expect(result.selectionCount).toBe(24);
    expect(result.exclusionCount).toBe(2);
    const [url, init] = fetcher.mock.calls[0]!;
    expect(url).toBe("/api/migration/nzbdav/connect");
    expect(init?.method).toBe("POST");
    const body = init?.body;
    if (typeof body !== "string") {
      throw new Error("Expected a JSON string request body");
    }
    expect(JSON.parse(body)).toEqual({
      packagePath: "/config/migration-input/nzbdav-canary",
      maxQueueDepth: 5,
      submitWorkers: 1,
    });
  });

  it("requires an exact immutable digest and selection count before Run", () => {
    const connection = {
      packageDigest: "b".repeat(64),
      selectionCount: 20,
      exclusionCount: 0,
      releaseCount: 4,
      categories: ["Migration-TV"],
    };
    expect(isRunConfirmationExact(connection, "b".repeat(64), "20")).toBe(true);
    expect(isRunConfirmationExact(connection, "c".repeat(64), "20")).toBe(false);
    expect(isRunConfirmationExact(connection, "b".repeat(64), "19")).toBe(false);
  });

  it("never enables plan generation while ambiguity remains", () => {
    const exact = { selectedCount: 6, exactCount: 6, exclusionCount: 0, ambiguityCount: 0 };
    expect(canGenerateCanaryPlan("complete", exact)).toBe(true);
    expect(canGenerateCanaryPlan("complete", { ...exact, exactCount: 5 })).toBe(false);
    expect(canGenerateCanaryPlan("complete", { ...exact, exclusionCount: 1 })).toBe(false);
    expect(canGenerateCanaryPlan("complete", { ...exact, ambiguityCount: 1 })).toBe(false);
    expect(canGenerateCanaryPlan("running", exact)).toBe(false);
  });

  it("enables reconciliation only for terminal incomplete correlation", () => {
    const incomplete = { selectedCount: 6, exactCount: 5 };
    expect(canReconcileNzbDav("complete", incomplete)).toBe(true);
    expect(canReconcileNzbDav("complete", { selectedCount: 6, exactCount: 6 })).toBe(false);
    expect(canReconcileNzbDav("running", incomplete)).toBe(false);
  });

  it("uses the dedicated plan endpoint", async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ actionableCount: 6 }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    await requestNzbDavPlan(fetcher);
    expect(fetcher).toHaveBeenCalledWith("/api/migration/nzbdav/canary-plan", {
      cache: "no-store",
      method: "POST",
    });
  });

  it("uses count-only full status and reconciliation endpoints", async () => {
    const statusFetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(
        JSON.stringify({
          recoveryStatus: "active",
          sourceLinkCount: 100,
          recoverableCount: 95,
          coverage: 0.95,
          batchCount: 1,
          selectedCount: 20,
          appliedCount: 10,
          validatedCount: 8,
          batches: [],
        }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );
    const status = await requestNzbDavFullStatus(statusFetcher);
    expect(status.coverage).toBe(0.95);
    expect(statusFetcher).toHaveBeenCalledWith("/api/migration/nzbdav/full/status", {
      cache: "no-store",
    });

    const reconcileFetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ exactCount: 6 }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    await requestNzbDavReconcile(reconcileFetcher);
    expect(reconcileFetcher).toHaveBeenCalledWith("/api/migration/nzbdav/reconcile", {
      cache: "no-store",
      method: "POST",
    });
  });
});
