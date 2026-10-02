// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { cleanup, render, screen, fireEvent } from "@testing-library/react";
import { LibraryFileModal } from "./file-modal";
import type { LibraryCatalogItem } from "~/clients/backend-client.server";
import { idleRegrabState, isActiveRegrab, regrabStatusLabel, type RegrabState } from "./regrab";
import { regrabQuery } from "./use-library-file-modal";

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
    Icon: () => null,
  };
});

vi.mock("~/components/media-preview", () => ({
  MediaPreview: ({ fileName, onClose }: { fileName: string; onClose: () => void }) => (
    <div>
      <span>preview:{fileName}</span>
      <button onClick={onClose}>close-preview</button>
    </div>
  ),
}));

const item: LibraryCatalogItem = {
  kind: "internal",
  davItemId: "11111111-1111-1111-1111-111111111111",
  displayName: "detail-film.mkv",
  contentPath: "/content/detail-film.mkv",
  size: 2048,
  mappingCount: 1,
  health: "healthy",
  mappings: [
    {
      linkPath: "movies/detail-film.mkv",
      targetText: "/mnt/.ids/x.mkv",
      mappingType: "internal",
      status: "valid",
    },
  ],
};

const baseProps = {
  item,
  libraryRoot: "/mnt/plex",
  quality: "1080p" as const,
  cachePercentage: 75,
  details: null,
  detailsLoading: false,
  detailsError: null,
  previewUrl: "/view/content/detail-film.mkv?downloadKey=k",
  canPrewarm: true,
  actionState: "idle" as const,
  feedback: null,
  onClose: () => {},
  onPreview: () => {},
  onRunHealthCheck: () => {},
  onRequeue: () => {},
  onPrewarm: () => {},
};

describe("LibraryFileModal", () => {
  afterEach(cleanup);
  it("renders file facts, mappings, and empty health state", () => {
    render(<LibraryFileModal {...baseProps} />);
    expect(screen.getByRole("dialog", { name: "detail-film.mkv" })).toBeTruthy();
    expect(screen.getAllByText("/mnt/plex/movies/detail-film.mkv")).toHaveLength(2);
    expect(screen.getByText("75%")).toBeTruthy();
    expect(screen.getByText(/no health checks recorded/i)).toBeTruthy();
  });

  it("opens preview and fires modal actions", () => {
    const onPreview = vi.fn();
    const onRequeue = vi.fn();
    render(<LibraryFileModal {...baseProps} onPreview={onPreview} onRequeue={onRequeue} />);
    fireEvent.click(screen.getByRole("button", { name: /preview/i }));
    fireEvent.click(screen.getByRole("button", { name: /requeue/i }));
    expect(onPreview).toHaveBeenCalledTimes(1);
    expect(onRequeue).toHaveBeenCalledTimes(1);
  });

  it("hides library-only details and repair actions for a file outside the library", () => {
    render(
      <LibraryFileModal
        {...baseProps}
        item={{ ...item, mappings: [], mappingCount: 0 }}
        libraryUnavailable="This file is not in the Media Library."
      />,
    );
    expect(screen.getByText("This file is not in the Media Library.")).toBeTruthy();
    expect(screen.getByText("Media file")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /requeue/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /run health check/i })).toBeNull();
    expect(screen.queryByText(/health history/i)).toBeNull();
    expect(screen.getByRole("button", { name: /prewarm/i })).toBeTruthy();
  });

  it("hides prewarm when native cache is inactive", () => {
    render(<LibraryFileModal {...baseProps} canPrewarm={false} />);
    expect(screen.queryByRole("button", { name: /prewarm/i })).toBeNull();
    expect(screen.getByText(/native cache is inactive/i)).toBeTruthy();
  });

  describe("Regrab", () => {
    const eligible: RegrabState = {
      ...idleRegrabState,
      preview: {
        eligible: true,
        releaseName: "South.Park.S01E07.2160p.WEB-DL-XEBEC",
        libraryPath: "/mnt/plex/TV/South Park/S01E07.mkv",
        oldLibraryLink: true,
        target: {
          app: "Sonarr",
          host: "http://sonarr:8989",
          mediaKind: "episode",
          mediaIds: [1800],
          fileId: 76844,
          label: "Sonarr episode 1800 on http://sonarr:8989",
        },
      },
    };

    it("is hidden when the page does not provide regrab state", () => {
      render(<LibraryFileModal {...baseProps} />);
      expect(screen.queryByRole("button", { name: /regrab/i })).toBeNull();
    });

    it("is disabled with the reason when the file has no Arr mapping", () => {
      render(
        <LibraryFileModal
          {...baseProps}
          regrab={{
            ...idleRegrabState,
            preview: {
              eligible: false,
              disabledReason: "Sonarr/Radarr does not list a media file at this library path.",
            },
          }}
        />,
      );
      const button = screen.getByRole<HTMLButtonElement>("button", { name: /regrab/i });
      expect(button.disabled).toBe(true);
      expect(
        screen.getByText(/regrab is unavailable: sonarr\/radarr does not list a media file/i),
      ).toBeTruthy();
    });

    it("asks for confirmation with the release and Arr target before regrabbing", () => {
      const onRegrab = vi.fn();
      const { rerender } = render(
        <LibraryFileModal {...baseProps} regrab={eligible} onRegrab={onRegrab} />,
      );
      const button = screen.getByRole<HTMLButtonElement>("button", { name: /regrab/i });
      expect(button.disabled).toBe(false);
      fireEvent.click(button);
      expect(onRegrab).toHaveBeenCalledTimes(1);

      const onRegrabConfirm = vi.fn();
      const onRegrabCancel = vi.fn();
      rerender(
        <LibraryFileModal
          {...baseProps}
          regrab={{ ...eligible, confirming: true }}
          onRegrab={onRegrab}
          onRegrabConfirm={onRegrabConfirm}
          onRegrabCancel={onRegrabCancel}
        />,
      );
      const dialog = screen.getByRole("alertdialog", { name: /confirm regrab/i });
      expect(dialog.textContent).toContain("South.Park.S01E07.2160p.WEB-DL-XEBEC");
      expect(dialog.textContent).toContain("Sonarr episode 1800 on http://sonarr:8989");
      expect(dialog.textContent).toContain("never its target");
      const buttons = Array.from(dialog.querySelectorAll("button"));
      fireEvent.click(buttons.find((b) => b.textContent === "Cancel")!);
      fireEvent.click(buttons.find((b) => b.textContent?.includes("Regrab"))!);
      expect(onRegrabCancel).toHaveBeenCalledTimes(1);
      expect(onRegrabConfirm).toHaveBeenCalledTimes(1);
    });

    it("shows the requested state and keeps the action disabled until replaced", () => {
      render(
        <LibraryFileModal
          {...baseProps}
          regrab={{
            ...idleRegrabState,
            preview: {
              eligible: false,
              disabledReason: "A regrab is already requested for this file.",
              request: {
                id: "r1",
                status: "requested",
                source: "manual",
                releaseName: "South.Park.S01E07",
                arrApp: "Sonarr",
                createdAt: "2026-10-02T18:00:00Z",
                requestedAt: "2026-10-02T18:01:00Z",
              },
            },
          }}
        />,
      );
      expect(screen.getByText("Regrab requested")).toBeTruthy();
      expect(screen.getByText(/in Sonarr/)).toBeTruthy();
      expect(screen.getByRole<HTMLButtonElement>("button", { name: /regrab/i }).disabled).toBe(
        true,
      );
    });

    it("labels regrab states and builds the lookup query", () => {
      expect(isActiveRegrab("pending")).toBe(true);
      expect(isActiveRegrab("replaced")).toBe(false);
      expect(regrabStatusLabel("search-withheld")).toBe("Regrab requested (search limited)");
      expect(regrabStatusLabel("skipped")).toBe("Regrab skipped");
      expect(regrabQuery(item)).toBe("davItemId=11111111-1111-1111-1111-111111111111");
      expect(
        regrabQuery({ ...item, kind: "external", davItemId: null, mappings: item.mappings }),
      ).toBe("linkPath=movies%2Fdetail-film.mkv");
      expect(regrabQuery({ ...item, davItemId: null, mappings: [] })).toBeNull();
    });
  });
});
