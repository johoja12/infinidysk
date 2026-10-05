// @vitest-environment jsdom
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useFileCache } from "./use-file-cache";

const snapshot = {
  available: true,
  length: 100,
  cachedBytes: 50,
  ranges: [{ offset: 50, count: 50 }],
  complete: true,
};
const response = (data: unknown) => ({ ok: true, json: async () => data });
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe("file modal cache ranges", () => {
  it("loads authoritative coverage and discards the previous file on selection change", async () => {
    let finishSecond: ((value: unknown) => void) | undefined;
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(response(snapshot))
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            finishSecond = resolve;
          }),
      );
    vi.stubGlobal("fetch", fetch);
    const { result, rerender } = renderHook(({ id }) => useFileCache(id), {
      initialProps: { id: "first" },
    });
    expect(result.current.loading).toBe(true);
    await waitFor(() => expect(result.current.data?.cachedBytes).toBe(50));
    const signal = fetch.mock.calls[0]![1].signal as AbortSignal;
    rerender({ id: "second" });
    expect(signal.aborted).toBe(true);
    expect(result.current.data).toBeNull();
    expect(result.current.loading).toBe(true);
    await act(async () => {
      finishSecond?.(response({ ...snapshot, cachedBytes: 0, ranges: [] }));
    });
    expect(result.current.data?.ranges).toEqual([]);
    expect(result.current.data?.cachedBytes).toBe(0);
  });

  it("refreshes warming coverage and stops polling after close", async () => {
    vi.useFakeTimers();
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(response(snapshot))
      .mockResolvedValue(
        response({ ...snapshot, cachedBytes: 100, ranges: [{ offset: 0, count: 100 }] }),
      );
    vi.stubGlobal("fetch", fetch);
    const { result, unmount } = renderHook(() => useFileCache("file"));
    await act(async () => {});
    expect(result.current.data?.cachedBytes).toBe(50);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5000);
    });
    expect(result.current.data?.cachedBytes).toBe(100);
    unmount();
    await vi.advanceTimersByTimeAsync(10000);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it("distinguishes unavailable and failed requests from an empty cache", async () => {
    const fetch = vi
      .fn()
      .mockResolvedValueOnce(response({ available: false }))
      .mockRejectedValueOnce(new Error("offline"));
    vi.stubGlobal("fetch", fetch);
    const { result, rerender } = renderHook(({ id }) => useFileCache(id), {
      initialProps: { id: "unavailable" },
    });
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.data).toBeNull();
    expect(result.current.error).toBeNull();
    rerender({ id: "failed" });
    await waitFor(() => expect(result.current.error).toBe("Cache ranges could not be loaded."));
    expect(result.current.data).toBeNull();
  });
});
