import { describe, expect, it, vi, beforeEach } from "vitest";
import { loader } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import { installFrontendRuntimeConfig } from "../../../server/runtime-config";

vi.mock("~/clients/backend-client.server", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/clients/backend-client.server")>();
  return {
    ...actual,
    backendClient: {
      ...actual.backendClient,
      getLibraryBrowse: vi.fn(),
      getNativeCacheStatus: vi.fn(),
    },
  };
});

beforeEach(() => {
  installFrontendRuntimeConfig({ frontendBackendApiKey: "test-api-key" });
  catalogMock().mockReset();
  catalogMock().mockResolvedValue({
    groups: [],
    totalGroups: 0,
    totalFiles: 0,
    showCount: 0,
    movieCount: 0,
    unmatchedCount: 0,
    page: 1,
    pageSize: 25,
  });
  nativeCacheMock().mockReset();
  nativeCacheMock().mockResolvedValue({ activeMode: "native" });
});

function requestFor(path: string): Request {
  return new Request(`http://localhost${path}`);
}

// Wraps the unbound-method lint (vi.mocked unwraps the method from its object).
// Fine here: the mock carries no `this` state.
function catalogMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getLibraryBrowse);
}

function nativeCacheMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getNativeCacheStatus);
}

describe("library loader", () => {
  it("passes search, filter, and pagination to the grouped catalog client", async () => {
    await loader({
      request: requestFor("/library?q=dune&type=broken&page=2"),
      params: {},
    } as never);

    expect(catalogMock()).toHaveBeenCalledWith({
      q: "dune",
      type: "broken",
      page: 2,
      pageSize: 25,
    });
  });

  it("clamps invalid page and pageSize to defaults", async () => {
    await loader({ request: requestFor("/library?page=0&pageSize=9999"), params: {} } as never);

    expect(catalogMock()).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }));
  });

  it("builds signed preview urls for internal rows", async () => {
    catalogMock().mockResolvedValue({
      groups: [
        {
          key: "movie:FILM",
          kind: "movie",
          name: "Film",
          fileCount: 1,
          mappingCount: 1,
          totalSize: 100,
          attentionCount: 0,
          files: [
            {
              episodeLabel: null,
              item: {
                kind: "internal",
                davItemId: "11111111-1111-1111-1111-111111111111",
                displayName: "film.mkv",
                contentPath: "/content/film.mkv",
                size: 100,
                mappingCount: 1,
                health: "healthy",
                mappings: [],
              },
            },
          ],
        },
      ],
      totalGroups: 1,
      totalFiles: 1,
      showCount: 0,
      movieCount: 1,
      unmatchedCount: 0,
      page: 1,
      pageSize: 25,
    });

    const data = await loader({ request: requestFor("/library"), params: {} } as never);

    expect(data.previewUrls["11111111-1111-1111-1111-111111111111"]).toMatch(
      /^\/view\/content\/film\.mkv\?downloadKey=.+/,
    );
    expect(data.nativeCacheActive).toBe(true);
  });
});
