// @vitest-environment jsdom
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { NzbDavMigrationView } from "./nzbdav-migration";

afterEach(cleanup);

describe("NzbDavMigrationView", () => {
  it("shows package evidence, isolation warnings, correlation reasons, and blocks ambiguous plans", async () => {
    const generatePlan = vi.fn();
    render(
      <NzbDavMigrationView
        form={{
          packagePath: "/config/migration-input/package",
          maxQueueDepth: 5,
          submitWorkers: 1,
        }}
        onFormChange={vi.fn()}
        connection={{
          packageDigest: "d".repeat(64),
          selectionCount: 22,
          exclusionCount: 2,
          releaseCount: 7,
          categories: ["Migration-TV"],
        }}
        sessionStatus="complete"
        categories={[{ source: "Migration-TV", target: "nzbdav-canary-tv", action: "migrate" }]}
        onCategoryChange={vi.fn()}
        correlation={{
          packageDigest: "d".repeat(64),
          selectedCount: 22,
          exclusionCount: 2,
          ambiguityCount: 1,
          exactCount: 21,
          rows: [
            {
              libraryRelativePath: "Migration-TV/Show/episode.mkv",
              legacyDavItemId: "legacy-id",
              expectedFileSize: 123,
              extractionStatus: "ready",
              exclusionReason: null,
              correlationStatus: "ambiguous",
              infiniDyskDavItemId: null,
              correlationEvidence: '{"candidates":["one","two"]}',
            },
          ],
        }}
        digestConfirmation="d"
        countConfirmation="22"
        onDigestConfirmationChange={vi.fn()}
        onCountConfirmationChange={vi.fn()}
        planReady={false}
        busy={null}
        error={null}
        onConnect={vi.fn()}
        onSaveCategories={vi.fn()}
        onScan={vi.fn()}
        onRun={vi.fn()}
        onLoadCorrelation={vi.fn()}
        onGeneratePlan={generatePlan}
      />,
    );

    expect(screen.getAllByText("22 selected")).toHaveLength(2);
    expect(screen.getAllByText("2 excluded")).toHaveLength(2);
    expect(screen.getByText(/must remain outside Plex/i)).toBeTruthy();
    expect(screen.getByText(/apply.*on nuc-1/i)).toBeTruthy();
    expect(screen.getByText(/dedicated empty InfiniDysk categories/i)).toBeTruthy();
    const row = screen.getByRole("row", { name: /episode\.mkv/i });
    expect(within(row).getByText("ambiguous")).toBeTruthy();
    expect(within(row).getByText(/candidates/i)).toBeTruthy();
    const button = screen.getByRole("button", { name: /generate canary plan/i });
    expect((button as HTMLButtonElement).disabled).toBe(true);
    await userEvent.click(button);
    expect(generatePlan).not.toHaveBeenCalled();
  });

  it("offers the immutable plan download after generation", () => {
    render(
      <NzbDavMigrationView
        form={{ packagePath: "", maxQueueDepth: 5, submitWorkers: 1 }}
        onFormChange={vi.fn()}
        connection={null}
        sessionStatus="complete"
        categories={[]}
        onCategoryChange={vi.fn()}
        correlation={{
          packageDigest: "e".repeat(64),
          selectedCount: 6,
          exclusionCount: 0,
          ambiguityCount: 0,
          exactCount: 6,
          rows: [],
        }}
        digestConfirmation=""
        countConfirmation=""
        onDigestConfirmationChange={vi.fn()}
        onCountConfirmationChange={vi.fn()}
        planReady
        busy={null}
        error={null}
        onConnect={vi.fn()}
        onSaveCategories={vi.fn()}
        onScan={vi.fn()}
        onRun={vi.fn()}
        onLoadCorrelation={vi.fn()}
        onGeneratePlan={vi.fn()}
      />,
    );

    expect(screen.getByRole("link", { name: /download canary plan/i }).getAttribute("href")).toBe(
      "/api/migration/nzbdav/canary-plan",
    );
  });
});
