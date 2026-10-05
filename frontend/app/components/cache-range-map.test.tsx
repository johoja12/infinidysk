// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { CacheRangeMap } from "./cache-range-map";

afterEach(cleanup);
describe("cache range map", () => {
  it("positions disjoint ranges by byte offset and shows gaps", () => {
    render(
      <CacheRangeMap
        file={{ length: 100 }}
        ranges={[
          { offset: 10, count: 20 },
          { offset: 80, count: 20 },
        ]}
        complete
      />,
    );
    const bar = screen.getByRole("img", { name: "2 verified ranges; 2 known gaps" });
    expect((bar.children[0] as HTMLElement).style.left).toBe("10%");
    expect((bar.children[0] as HTMLElement).style.width).toBe("20%");
    expect((bar.children[1] as HTMLElement).style.left).toBe("80%");
    expect(screen.getByText("Known gaps")).toBeTruthy();
  });

  it.each([
    [[], "0 verified ranges; 1 known gaps"],
    [[{ offset: 0, count: 100 }], "1 verified ranges; 0 known gaps"],
  ])("renders empty and full coverage", (ranges, label) => {
    render(<CacheRangeMap file={{ length: 100 }} ranges={ranges} complete />);
    expect(screen.getByRole("img", { name: label })).toBeTruthy();
  });

  it("does not label unloaded regions as known gaps", () => {
    render(
      <CacheRangeMap file={{ length: 100 }} ranges={[{ offset: 0, count: 10 }]} complete={false} />,
    );
    expect(
      screen.getByRole("img", { name: "1 verified ranges; more ranges available" }),
    ).toBeTruthy();
    expect(screen.queryByText("Known gaps")).toBeNull();
  });
});
