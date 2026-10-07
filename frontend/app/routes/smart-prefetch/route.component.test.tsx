// @vitest-environment jsdom
import type { ReactNode } from "react";
import { act, cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import SmartPrefetchActivityPage from "./route";

vi.mock("~/auth/authorization", () => ({ useIsReadOnly: () => true }));
vi.mock("~/components/prefetch-queue", () => ({ PrefetchQueue: () => null }));
// jsdom has no <dialog>.showModal(), so render the shared modal as a plain dialog role.
vi.mock("~/components/ui", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/components/ui")>();
  return {
    ...actual,
    Modal: ({ open, title, children }: { open: boolean; title: string; children: ReactNode }) =>
      open ? (
        <div role="dialog" aria-label={title}>
          {children}
        </div>
      ) : null,
  };
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const now = Date.now();
const response = {
  available: true,
  healthy: true,
  paused: false,
  dailyBudgetUsed: 2_000_000_000,
  settings: { dailyByteBudget: 10_000_000_000, maxConcurrentJobs: 2 },
  jobs: [
    {
      id: "a",
      itemId: "item-a",
      displayName: "Movie warming",
      source: "Plex",
      trigger: "plex",
      state: "running",
      start: 0,
      length: 0,
      committedBytes: 1_000_000_000,
      fileSize: 2_000_000_000,
      updated: now,
    },
    {
      id: "b",
      itemId: "item-b",
      displayName: "Series finished",
      source: "Plex",
      trigger: "plex",
      state: "completed",
      start: 0,
      length: 0,
      committedBytes: 2_000_000_000,
      fileSize: 2_000_000_000,
      updated: now,
    },
  ],
};

describe("Smart Prefetch activity page", () => {
  it("separates active jobs from recent history and shows real budget accounting", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(response) }),
    );
    const router = createMemoryRouter(
      [
        { path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> },
        { path: "/settings", element: <div>Settings</div> },
      ],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("Movie warming")).toBeTruthy();
    expect(screen.queryByText("Series finished")).toBeNull();
    expect(screen.getByText("Daily provider budget").parentElement?.textContent).toContain(
      "2 GB / 10 GB",
    );
    await userEvent.setup().click(screen.getByRole("tab", { name: /Warming history/ }));
    expect(screen.getByText("Series finished")).toBeTruthy();
    expect(screen.queryByText("Movie warming")).toBeNull();
    expect(screen.getByText("Completed today")).toBeTruthy();
    expect(screen.queryByText("Advanced warming controls")).toBeNull();
    expect(screen.queryByText("About this history")).toBeNull();
    expect(screen.queryByText("Why a job might wait")).toBeNull();
  });

  it("shows per-job warming speed and the median for recorded history", async () => {
    const recorded = {
      ...response,
      jobs: [
        response.jobs[1],
        {
          ...response.jobs[1],
          id: "c",
          itemId: "item-c",
          displayName: "Timed movie",
          activeMs: 185_000,
          warmedBytes: 14_200_000 * 185,
          startedAt: now - 200_000,
          finishedAt: now,
        },
      ],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(recorded) }),
    );
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    await userEvent.setup().click(await screen.findByRole("tab", { name: /Warming history/ }));
    const timed = screen.getByRole("button", { name: /Timed movie/ });
    expect(timed.textContent).toContain("14.2 MB/s · 3m 05s");
    // A job recorded before timing existed shows a dash instead of a speed.
    expect(screen.getByRole("button", { name: /Series finished/ }).textContent).toContain("—");
    expect(screen.getByText(/median 14\.2 MB\/s · 2 jobs/)).toBeTruthy();
  });

  it("splits history into prefetch warming and playback gap fills with a definite reason", async () => {
    const MiB = 1024 * 1024;
    const history = {
      ...response,
      settings: { ...response.settings, finishWatchedEnabled: false },
      jobs: [
        {
          id: "backfill",
          itemId: "item-d",
          displayName: "A.Good.Girls.Guide.S02E02",
          source: "Playback not yet cached",
          trigger: "backfill",
          state: "completed",
          start: 100 * MiB,
          length: 4 * MiB,
          committedBytes: 50_300_000,
          fileSize: 6_900_000_000,
          isRangeJob: true,
          rangeBytes: 4 * MiB,
          rangeCachedBytes: 4 * MiB,
          missReason: "buffers-full",
          viewerUser: "alex",
          viewerPlayer: "Living Room TV",
          mediaDurationMs: 52 * 60_000,
          updated: now,
        },
        {
          id: "damaged",
          itemId: "item-e",
          displayName: "South.Park.S01E07",
          source: "Selected Plex hub/collection",
          trigger: "plex:x:source:y:episode:z",
          state: "failed",
          start: 0,
          length: 0,
          committedBytes: 0,
          fileSize: 3_000_000_000,
          error: "Release damaged on Usenet. Queued for repair.",
          failureCode: "source-damaged",
          remedy: "repair-queued",
          updated: now,
        },
        {
          id: "queued-backfill",
          itemId: "item-f",
          displayName: "Queued backfill",
          source: "Playback not yet cached",
          trigger: "backfill",
          state: "queued",
          start: 0,
          length: 8 * MiB,
          committedBytes: 0,
          fileSize: 6_900_000_000,
          isRangeJob: true,
          rangeBytes: 8 * MiB,
          rangeCachedBytes: 0,
          updated: now,
        },
      ],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(history) }),
    );
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    const queued = await screen.findByRole("button", { name: /Queued backfill/ });
    expect(queued.textContent).toContain(
      "Playback streamed 8 MiB at 0% into the file directly from Usenet — filling it in now",
    );
    expect(queued.textContent).toContain("Reason not recorded");

    const user = userEvent.setup();
    await user.click(screen.getByRole("tab", { name: /Warming history/ }));
    // Prefetch warming is the default: gap fills are not mixed in.
    expect(screen.queryByRole("button", { name: /A\.Good\.Girls\.Guide/ })).toBeNull();
    const damaged = screen.getByRole("button", { name: /South\.Park/ });
    expect(damaged.textContent).toContain("Release damaged on Usenet");
    expect(within(damaged).getByText("Queued for repair")).toBeTruthy();
    expect(
      screen.getByText(
        /The cache keeps what was played plus the files your prefetch policies select/,
      ),
    ).toBeTruthy();
    expect(screen.getByText(/Partially cached files fill in when they are played/)).toBeTruthy();

    await user.click(screen.getByRole("tab", { name: /Playback gap fills/ }));
    expect(screen.queryByRole("button", { name: /South\.Park/ })).toBeNull();
    const backfill = screen.getByRole("button", { name: /A\.Good\.Girls\.Guide/ });
    expect(backfill.textContent).toContain("Cache buffers full");
    expect(backfill.textContent).toContain("Living Room TV · alex");
    expect(backfill.textContent).toContain(
      "Playback streamed 4 MiB at ~0:47 of 52:00 directly from Usenet.",
    );
    expect(backfill.textContent).toContain("File overall: 1% cached");
    expect(backfill.textContent).not.toContain("✓");
    expect(within(backfill).getByLabelText("Gap at 2% of the file")).toBeTruthy();

    await user.selectOptions(
      screen.getByRole("combobox", { name: /Filter history/ }),
      "write-failed",
    );
    expect(screen.queryByRole("button", { name: /A\.Good\.Girls\.Guide/ })).toBeNull();
    expect(screen.getByText("No matching jobs.")).toBeTruthy();
  });

  it("shows live speed for running jobs and source bubbles in activity and history", async () => {
    const live = {
      ...response,
      jobs: [
        {
          ...response.jobs[0],
          displayName: "Ted_Lasso_S04E07",
          committedBytes: 3_000_000_000,
          fileSize: 7_800_000_000,
          activeMs: 200_000,
          warmedBytes: 14_200_000 * 200,
          recentBytesPerSecond: 18_400_000,
          lastProgressAt: now,
          stalled: false,
          sources: [{ label: "Popular TV This Year", category: "plex-source" }],
          sourceCount: 1,
        },
        {
          ...response.jobs[0],
          id: "stalled",
          itemId: "item-s",
          displayName: "Stuck movie",
          stalled: true,
          lastProgressAt: now - 90_000,
          sources: [{ label: "Playing now", category: "plex-realtime" }],
          sourceCount: 1,
        },
        {
          ...response.jobs[1],
          sources: [
            { label: "Next episode · history", category: "plex-history-next" },
            { label: "Manual", category: "manual" },
            { label: "Watch history", category: "plex-history" },
          ],
          sourceCount: 3,
        },
      ],
    };
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve(live) }),
    );
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    const running = await screen.findByRole("button", { name: /Ted_Lasso_S04E07/ });
    expect(running.textContent).toContain("18.4 MB/s now · 14.2 MB/s avg · ETA 4m 21s");
    expect(within(running).getByText("Popular TV This Year").className).toContain("badge-primary");
    expect(screen.getByRole("button", { name: /Stuck movie/ }).textContent).toMatch(
      /Stalled · no progress for 1m 3\ds/,
    );

    await userEvent.setup().click(screen.getByRole("tab", { name: /Warming history/ }));
    const finished = screen.getByRole("button", { name: /Series finished/ });
    expect(within(finished).getByText("Next episode · history")).toBeTruthy();
    expect(within(finished).getByText("+1")).toBeTruthy();
    expect(finished.textContent).not.toContain("now ·");
  });

  it("opens the shared media file modal from a history row with the keyboard", async () => {
    const details = {
      davItemId: "item-b",
      name: "Series.S01E01.1080p.mkv",
      contentPath: "/content/Series.S01E01.1080p.mkv",
      size: 2_000_000_000,
      mappings: [
        {
          linkPath: "tv/Series/Series.S01E01.1080p.mkv",
          targetText: "/mnt/.ids/item-b",
          mappingType: "internal",
          status: "valid",
        },
      ],
    };
    const fetchMock = vi.fn((input: string) =>
      Promise.resolve({
        ok: true,
        json: () =>
          Promise.resolve(
            input.includes("/native-cache/file-ranges")
              ? {
                  available: true,
                  length: details.size,
                  cachedBytes: details.size,
                  ranges: [{ offset: 0, count: details.size }],
                  complete: true,
                }
              : input.includes("/library-file")
                ? {
                    details,
                    previewUrl: "/view/content/Series.S01E01.1080p.mkv?downloadKey=k",
                    libraryRoot: "/mnt/plex",
                    unavailableReason: null,
                  }
                : response,
          ),
      }),
    );
    vi.stubGlobal("fetch", fetchMock);
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("tab", { name: /Warming history/ }));
    screen.getByRole("button", { name: /Series finished/ }).focus();
    await user.keyboard("{Enter}");

    const dialog = await screen.findByRole("dialog");
    expect(fetchMock).toHaveBeenCalledWith("/library-file?davItemId=item-b");
    // Library path and mapping list both show the full link path once details load.
    expect(
      await within(dialog).findAllByText("/mnt/plex/tv/Series/Series.S01E01.1080p.mkv"),
    ).toHaveLength(2);
    expect(within(dialog).getByRole("button", { name: /Preview/ })).toBeTruthy();
    expect(within(dialog).getByText("100%")).toBeTruthy();
    expect(within(dialog).getByRole("button", { name: /Requeue repair/ })).toBeTruthy();
  });

  it("degrades the modal when the file is not in the Media Library", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn((input: string) =>
        Promise.resolve({
          ok: true,
          json: () =>
            Promise.resolve(
              input.includes("/native-cache/file-ranges")
                ? { available: false }
                : input.includes("/library-file")
                  ? {
                      details: null,
                      previewUrl: null,
                      libraryRoot: null,
                      unavailableReason: "This file is not in the Media Library.",
                    }
                  : response,
            ),
        }),
      ),
    );
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    await userEvent.setup().click(await screen.findByRole("button", { name: /Movie warming/ }));

    const dialog = await screen.findByRole("dialog");
    expect(await within(dialog).findByText("This file is not in the Media Library.")).toBeTruthy();
    expect(within(dialog).getByText("Movie warming")).toBeTruthy();
    expect(within(dialog).getByText("Unavailable")).toBeTruthy();
    expect(within(dialog).queryByRole("button", { name: /Requeue repair/ })).toBeNull();
    expect(within(dialog).queryByRole("button", { name: /Run health check/ })).toBeNull();
  });
  it("shows active jobs while range coverage is stalled", async () => {
    const range = {
      ...response.jobs[0],
      start: 0,
      length: 4096,
      rangeBytes: null,
      rangeCachedBytes: null,
    };
    const fetch = vi.fn((url: string) =>
      url.includes("/coverage?")
        ? new Promise<Response>(() => {})
        : Promise.resolve(new Response(JSON.stringify({ ...response, jobs: [range] }))),
    );
    vi.stubGlobal("fetch", fetch);
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    expect(await screen.findByText("Movie warming")).toBeTruthy();
    expect(fetch.mock.calls[0]![0]).toContain("includeCoverage=false");
    expect(screen.getByText(/checking current coverage/)).toBeTruthy();
  });

  it("does not overlap status polls when a status request is slow", async () => {
    vi.useFakeTimers();
    try {
      const fetch = vi.fn(() => new Promise<Response>(() => {}));
      vi.stubGlobal("fetch", fetch);
      const router = createMemoryRouter(
        [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
        { initialEntries: ["/smart-prefetch"] },
      );
      render(<RouterProvider router={router} />);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000);
      });
      expect(fetch).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });
  it("accepts matching range coverage after running-job progress updates", async () => {
    const range = {
      ...response.jobs[0],
      start: 0,
      length: 4096,
      rangeBytes: null,
      rangeCachedBytes: null,
    };
    const fetch = vi.fn((url: string) => {
      const body = url.includes("/coverage?")
        ? {
            coverage: {
              a: {
                updated: now + 100,
                start: 0,
                length: 4096,
                rangeBytes: 4096,
                rangeCachedBytes: 4096,
              },
            },
          }
        : { ...response, jobs: [range] };
      return Promise.resolve(new Response(JSON.stringify(body)));
    });
    vi.stubGlobal("fetch", fetch);
    const router = createMemoryRouter(
      [{ path: "/smart-prefetch", element: <SmartPrefetchActivityPage /> }],
      { initialEntries: ["/smart-prefetch"] },
    );
    render(<RouterProvider router={router} />);
    expect(await screen.findByText(/Cached the 4 KiB requested range/)).toBeTruthy();
  });
});
