// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { SourceBubbles, sourceBubbleClass } from "./source-bubbles";

afterEach(cleanup);

describe("Smart Prefetch source bubbles", () => {
  it("names the concrete source with a category colour", () => {
    render(
      <SourceBubbles
        sources={[{ label: "Popular TV This Year", category: "plex-source" }]}
        sourceCount={1}
        fallback="Selected Plex hub/collection"
      />,
    );
    const bubble = screen.getByText("Popular TV This Year");
    expect(bubble.className).toContain("badge-primary");
    expect(bubble.className).toContain("badge-soft");
    expect(bubble.getAttribute("data-category")).toBe("plex-source");
  });

  it("gives each category a distinct colour", () => {
    const categories = [
      "plex-source",
      "plex-realtime",
      "plex-realtime-next",
      "plex-history",
      "plex-history-next",
      "backfill",
      "finish-watched",
      "manual",
      "read",
    ];
    expect(new Set(categories.map(sourceBubbleClass)).size).toBe(categories.length);
    expect(sourceBubbleClass("unknown")).toBe("badge-ghost");
  });

  it("shows two bubbles and a +N count with the rest in its tooltip", () => {
    render(
      <SourceBubbles
        sources={[
          { label: "Playing now", category: "plex-realtime" },
          { label: "Trending", category: "plex-source" },
          { label: "Manual", category: "manual" },
        ]}
        sourceCount={4}
        fallback="Background warming"
      />,
    );
    expect(screen.getByText("Playing now")).toBeTruthy();
    expect(screen.getByText("Trending")).toBeTruthy();
    expect(screen.queryByText("Manual")).toBeNull();
    expect(screen.getByText("+2").getAttribute("title")).toBe("Manual");
  });

  it("falls back to the generic source label for older responses", () => {
    render(<SourceBubbles fallback="Playback not yet cached" />);
    expect(screen.getByText("Playback not yet cached").className).toContain("badge-ghost");
  });
});
