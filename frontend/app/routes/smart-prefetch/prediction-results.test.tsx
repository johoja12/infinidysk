// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { PredictionResults } from "./prediction-results";
import type { LibraryFileModalController } from "~/components/library-file-modal/use-library-file-modal";
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});
it("filters live predictions by viewer and current coverage, and opens the selected file without warming", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>((input) => {
    const url = input instanceof Request ? input.url : input.toString();
    const data = url.includes("/predictions")
      ? {
          predictions: [
            {
              itemId: "file-a",
              displayName: "Voyager.mkv",
              showTitle: "Voyager",
              fileSize: 100,
              season: 1,
              episode: 16,
              episodeTitle: "Learning Curve",
              viewer: "Sam",
              attribution: { label: "Next episode · history · Sam", category: "plex-history-next" },
              reason: "Next unwatched episode",
              eligible: true,
            },
            {
              itemId: "file-b",
              displayName: "Silo.mkv",
              showTitle: "Silo",
              fileSize: 100,
              viewer: "Alice",
              attribution: {
                label: "Next episode · realtime · Alice",
                category: "plex-realtime-next",
              },
              reason: "Next episode",
              eligible: true,
            },
          ],
        }
      : {
          available: true,
          length: 100,
          cachedBytes: url.includes("file-a") ? 100 : 20,
          complete: true,
          ranges: [{ offset: 0, count: url.includes("file-a") ? 100 : 20 }],
        };
    return Promise.resolve(new Response(JSON.stringify(data)));
  });
  vi.stubGlobal("fetch", fetch);
  const openByDavItemId = vi.fn();
  render(
    <PredictionResults
      jobs={[]}
      refreshKey={0}
      modal={{ openByDavItemId } as unknown as LibraryFileModalController}
    />,
  );
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Fully cached · 1" })).toBeTruthy(),
  );
  fireEvent.change(screen.getByRole("combobox", { name: "Filter prediction viewer" }), {
    target: { value: "Alice" },
  });
  expect(screen.getByText("Silo").closest("article")!.hidden).toBe(false);
  expect(screen.getByText("Voyager").closest("article")!.hidden).toBe(true);
  fireEvent.change(screen.getByRole("combobox", { name: "Filter prediction viewer" }), {
    target: { value: "all" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Fully cached · 1" }));
  expect(screen.getByText("Silo").closest("article")!.hidden).toBe(true);
  fireEvent.click(screen.getAllByRole("button", { name: "Open file details" })[0]!);
  expect(openByDavItemId).toHaveBeenCalledWith(
    "file-a",
    expect.objectContaining({ cachePercentage: 100 }),
  );
  expect(
    fetch.mock.calls.every(([, options]) => !options?.method || options.method === "GET"),
  ).toBe(true);
});

it("renders saved predictions during failed refreshes and identifies only affected viewers", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn((input: RequestInfo | URL) =>
      Promise.resolve(
        new Response(
          JSON.stringify(
            (input instanceof Request ? input.url : input.toString()).includes("/predictions")
              ? {
                  hasSnapshot: true,
                  updatedAt: "2026-10-05T00:00:00Z",
                  refreshing: true,
                  stale: true,
                  error: "Plex refresh failed",
                  predictions: [
                    {
                      itemId: "00000000-0000-0000-0000-000000000000",
                      displayName: "Voyager",
                      viewer: "Alice",
                      attribution: { label: "Next episode · Alice", category: "plex-history-next" },
                      reason: "Mapping unresolved",
                      watchedStatus: "unconnected",
                      watchedWarning: "Connect this profile",
                      serverName: "Living room",
                    },
                  ],
                }
              : { available: false },
          ),
        ),
      ),
    ),
  );
  render(<PredictionResults jobs={[]} refreshKey={0} modal={{} as LibraryFileModalController} />);
  await screen.findByText("Voyager");
  expect(screen.getByRole("alert").textContent).toContain("Showing the last successful results");
  expect(screen.getByText(/Updated .*Refreshing/).textContent).toContain("Previous results");
  expect(screen.getByText(/Alice · Living room: Chronological candidate/)).toBeTruthy();
  expect(screen.getByRole("link", { name: "Plex connections" }).getAttribute("href")).toBe(
    "/settings?tab=streaming#plex-connections",
  );
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Not in library · 1" })).toBeTruthy(),
  );
  expect(screen.queryByText("0%")).toBeNull();
});

it("shows a first-refresh failure instead of an empty success or permanent loading", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            predictions: [],
            hasSnapshot: false,
            refreshing: false,
            error: "Retry the refresh",
          }),
        ),
      ),
    ),
  );
  render(<PredictionResults jobs={[]} refreshKey={0} modal={{} as LibraryFileModalController} />);
  await screen.findByText("Prediction refresh failed. Retrying shortly.");
  expect(screen.queryByText(/No next-episode predictions/)).toBeNull();
  expect(screen.queryByText("Loading prediction results…")).toBeNull();
});

it("distinguishes loading from a successfully empty prediction snapshot", async () => {
  let resolve!: (response: Response) => void;
  vi.stubGlobal(
    "fetch",
    vi.fn(
      () =>
        new Promise<Response>((complete) => {
          resolve = complete;
        }),
    ),
  );
  render(<PredictionResults jobs={[]} refreshKey={0} modal={{} as LibraryFileModalController} />);
  expect(screen.getByText("Loading prediction results…")).toBeTruthy();
  resolve(
    new Response(
      JSON.stringify({
        predictions: [],
        hasSnapshot: true,
        updatedAt: "2026-10-05T00:00:00Z",
        refreshing: false,
      }),
    ),
  );
  await screen.findByText(/No next-episode predictions/);
  expect(screen.queryByText("Loading prediction results…")).toBeNull();
});
