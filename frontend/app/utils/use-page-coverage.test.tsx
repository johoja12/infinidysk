// @vitest-environment jsdom
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { usePageCoverage } from "./use-page-coverage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it("leaves content available while coverage is pending, and requests only bounded pages", async () => {
  let resolve!: (value: Response) => void;
  const fetch = vi
    .fn<(url: string, init: RequestInit) => Promise<Response>>()
    .mockReturnValueOnce(
      new Promise<Response>((done) => {
        resolve = done;
      }),
    )
    .mockResolvedValue(new Response(JSON.stringify({ coverage: { c: 50 } })));
  vi.stubGlobal("fetch", fetch);
  const { result } = renderHook(() =>
    usePageCoverage<number>("/coverage", "ids", ["a", "b", "c"], "snapshot", 2),
  );
  expect(result.current.pending).toBe(true);
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0]![0]).toBe("/coverage?ids=a%2Cb");
  await act(async () => {
    resolve(new Response(JSON.stringify({ coverage: { a: 100, b: 0 } })));
    await Promise.resolve();
  });
  await waitFor(() => expect(result.current.pending).toBe(false));
  expect(result.current.values).toEqual({ a: 100, b: 0, c: 50 });
  expect(fetch).toHaveBeenCalledTimes(2);
});

it("aborts replaced page reads and rejects their late coverage", async () => {
  let firstResolve!: (value: Response) => void;
  const fetch = vi
    .fn<(url: string, init: RequestInit) => Promise<Response>>()
    .mockReturnValueOnce(
      new Promise<Response>((done) => {
        firstResolve = done;
      }),
    )
    .mockResolvedValue(new Response(JSON.stringify({ coverage: { a: 0 } })));
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(
    ({ revision }) => usePageCoverage<number>("/coverage", "ids", ["a"], revision, 25),
    { initialProps: { revision: 1 } },
  );
  const oldSignal = fetch.mock.calls[0]![1].signal as AbortSignal;
  rerender({ revision: 2 });
  expect(oldSignal.aborted).toBe(true);
  await waitFor(() => expect(result.current.values["a"]).toBe(0));
  await act(async () => {
    firstResolve(new Response(JSON.stringify({ coverage: { a: 100 } })));
    await Promise.resolve();
  });
  expect(result.current.values["a"]).toBe(0);
});

it("finishes with unavailable coverage after a failure", async () => {
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("unavailable")));
  const { result } = renderHook(() => usePageCoverage<number>("/coverage", "ids", ["a"], 1, 25));
  await waitFor(() => expect(result.current.pending).toBe(false));
  expect(result.current.values).toEqual({});
});

it("coalesces status refreshes without aborting a slow history coverage scan", async () => {
  let resolve!: (value: Response) => void;
  const fetch = vi
    .fn<(url: string, init: RequestInit) => Promise<Response>>()
    .mockReturnValueOnce(
      new Promise<Response>((done) => {
        resolve = done;
      }),
    )
    .mockResolvedValue(new Response(JSON.stringify({ coverage: { a: 0 } })));
  vi.stubGlobal("fetch", fetch);
  const { result, rerender } = renderHook(
    ({ revision }) => usePageCoverage<number>("/coverage", "ids", ["a"], revision, 25, true),
    { initialProps: { revision: 1 } },
  );
  const signal = fetch.mock.calls[0]![1].signal as AbortSignal;
  rerender({ revision: 2 });
  expect(signal.aborted).toBe(false);
  expect(fetch).toHaveBeenCalledTimes(1);
  await act(async () => {
    resolve(new Response(JSON.stringify({ coverage: { a: 100 } })));
    await Promise.resolve();
  });
  await waitFor(() => expect(result.current.values["a"]).toBe(0));
  expect(fetch).toHaveBeenCalledTimes(2);
});
