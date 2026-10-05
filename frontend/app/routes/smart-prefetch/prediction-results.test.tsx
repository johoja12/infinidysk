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
  const fetch = vi.fn<typeof globalThis.fetch>(async (input) => {
    const url = String(input);
    const data = url.includes("/preview")
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
    return new Response(JSON.stringify(data));
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
