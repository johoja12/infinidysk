import { describe, expect, it, vi } from "vitest";
import {
  DEFAULT_NZBDAV_CONNECT_FORM,
  canGenerateCanaryPlan,
  isRunConfirmationExact,
  requestNzbDavConnect,
  requestNzbDavPlan,
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
    expect(JSON.parse(String(init?.body))).toEqual({
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
    expect(canGenerateCanaryPlan("complete", { ambiguityCount: 0 })).toBe(true);
    expect(canGenerateCanaryPlan("complete", { ambiguityCount: 1 })).toBe(false);
    expect(canGenerateCanaryPlan("running", { ambiguityCount: 0 })).toBe(false);
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
});
