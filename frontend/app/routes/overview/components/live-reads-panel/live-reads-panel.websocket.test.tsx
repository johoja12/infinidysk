// @vitest-environment jsdom
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ActiveRead } from "~/clients/backend-client.server";

const topic = vi.hoisted(() => ({ handler: null as ((message: string) => void) | null }));
vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: (_topic: string, _kind: string, handler: (message: string) => void) => {
    topic.handler = handler;
  },
}));

const { LiveReadsPanel } = await import("./live-reads-panel");

function burst(id: string, startedAt: number, bytesRead: number): ActiveRead {
  return {
    id,
    fileName: "Coyote.vs.Acme.2026.2160p.mkv",
    path: "/content/movies/Coyote.vs.Acme.2026.2160p.mkv",
    startedAt,
    lastActivityAt: startedAt,
    bytesRead,
    bytesFetched: 0,
    currentOffset: bytesRead,
    fileSize: 19_174_970_520,
    clientIp: "192.168.20.80",
    clientUserAgent: "rclone/v1.72.0",
    providers: [],
  };
}

const send = (reads: ActiveRead[]) => act(() => topic.handler?.(JSON.stringify({ reads })));

describe("LiveReadsPanel playback bursts", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    topic.handler = null;
  });

  it("keeps one cached playback row across rclone bursts and expires it after the grace period", async () => {
    const { container } = render(<LiveReadsPanel />);
    const now = Date.now();

    send([burst("a", now, 80 * 1024 * 1024)]);
    expect(container.querySelectorAll("li")).toHaveLength(1);
    expect(container.textContent).toContain("from cache");
    expect(container.textContent).toContain("1 active");

    // The burst ends; the backend goes quiet once nothing is being read.
    vi.advanceTimersByTime(4_000);
    send([]);
    expect(container.querySelectorAll("li")).toHaveLength(1);
    expect(container.textContent).toContain("between reads");
    expect(container.textContent).not.toContain("active");

    // The next burst continues the same row.
    vi.advanceTimersByTime(5_000);
    send([burst("b", now + 9_000, 90 * 1024 * 1024)]);
    expect(container.querySelectorAll("li")).toHaveLength(1);
    expect(container.textContent).not.toContain("between reads");

    // After the last burst, no further messages arrive; the idle row expires on its own.
    vi.advanceTimersByTime(1_000);
    send([]);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(29_000);
    });
    expect(container.querySelectorAll("li")).toHaveLength(1);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3_000);
    });
    expect(container.querySelectorAll("li")).toHaveLength(0);
    expect(container.textContent).toContain("No files are being read right now.");
  });
});
