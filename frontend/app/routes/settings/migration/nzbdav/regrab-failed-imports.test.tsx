// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { parseFailureReports, RegrabFailedImports } from "./regrab-failed-imports";

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function urlOf(input: RequestInfo | URL): string {
  return typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const report = {
  batchIndex: 3,
  failures: [
    {
      sourceReleaseId: "r1",
      legacyDavItemId: "11111111-1111-1111-1111-111111111111",
      libraryRelativePath: "TV-HD/Show/S01E01.mkv",
      submissionState: "failed",
      reason: "Missing articles: 1 important file(s) have missing segments",
    },
  ],
};

function reportFile(body: unknown): File {
  return {
    name: "import-failures.json",
    text: () => Promise.resolve(JSON.stringify(body)),
  } as File;
}

describe("parseFailureReports", () => {
  it("flattens reports and copies each batch index onto its failures", () => {
    const failures = parseFailureReports([
      JSON.stringify(report),
      JSON.stringify({ batchIndex: 4, failures: [{ legacyDavItemId: "x", batchIndex: 9 }] }),
    ]);
    expect(failures).toHaveLength(2);
    expect(failures[0]!.batchIndex).toBe(3);
    expect(failures[1]!.batchIndex).toBe(9);
  });

  it("rejects files that are not import-failure reports", () => {
    expect(() => parseFailureReports(['{"rows": []}'])).toThrow(/no failures array/);
  });
});

describe("RegrabFailedImports", () => {
  it("requires a dry run and queues at most the limit", async () => {
    fetchMock.mockImplementation((input, init) => {
      const url = urlOf(input);
      if (url.endsWith("/api/arr-regrab/migration-failures") && !init?.method)
        return Promise.resolve(jsonResponse({ status: true, statusCounts: { requested: 2 } }));
      if (url.endsWith("/dry-run"))
        return Promise.resolve(
          jsonResponse({
            status: true,
            total: 4,
            eligible: 3,
            alreadyRequested: 1,
            skipped: { "source-link-changed": 1 },
            previewToken: "token-1",
          }),
        );
      return Promise.resolve(
        jsonResponse({
          status: true,
          message: "Queued 2 regrab requests.",
          queued: 2,
          remaining: 1,
        }),
      );
    });

    render(<RegrabFailedImports />);
    expect(await screen.findByText("requested: 2")).toBeTruthy();

    const regrab = screen.getByRole<HTMLButtonElement>("button", { name: /^regrab/i });
    expect(regrab.disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/saved import-failures reports/i), {
      target: { files: [reportFile(report)] },
    });
    expect(await screen.findByText("1 failed imports loaded.")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: /dry run/i }));
    expect(await screen.findByText(/1 already replaced/)).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/limit per run/i), { target: { value: "2" } });
    const run = screen.getByRole<HTMLButtonElement>("button", { name: "Regrab 2" });
    expect(run.disabled).toBe(false);
    fireEvent.click(run);

    expect(await screen.findByText(/Queued 2 regrab requests\. 1 more can be queued/)).toBeTruthy();
    const runCall = fetchMock.mock.calls.find(
      ([url, init]) => urlOf(url).endsWith("/api/arr-regrab/migration-failures") && !!init?.method,
    );
    const sent = runCall?.[1]?.body;
    expect(typeof sent).toBe("string");
    const body = JSON.parse(sent as string) as {
      limit: number;
      previewToken: string;
      failures: { batchIndex: number }[];
    };
    expect(body.limit).toBe(2);
    expect(body.previewToken).toBe("token-1");
    expect(body.failures[0]!.batchIndex).toBe(3);
    await waitFor(() =>
      expect(screen.getByRole<HTMLButtonElement>("button", { name: /^regrab/i }).disabled).toBe(
        true,
      ),
    );
  });

  it("shows server errors from the dry run", async () => {
    fetchMock.mockImplementation((input) =>
      Promise.resolve(
        urlOf(input).endsWith("/dry-run")
          ? jsonResponse({ status: false, error: "Provide at least one failed import." }, 400)
          : jsonResponse({ status: true, statusCounts: {} }),
      ),
    );

    render(<RegrabFailedImports />);
    fireEvent.change(screen.getByLabelText(/saved import-failures reports/i), {
      target: { files: [reportFile(report)] },
    });
    await screen.findByText("1 failed imports loaded.");
    fireEvent.click(screen.getByRole("button", { name: /dry run/i }));
    expect((await screen.findByRole("alert")).textContent).toContain(
      "Provide at least one failed import.",
    );
  });
});
