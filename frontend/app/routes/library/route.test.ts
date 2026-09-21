import { describe, expect, it, vi, beforeEach } from "vitest";
import { loader } from "./route";
import { backendClient } from "~/clients/backend-client.server";
import { installFrontendRuntimeConfig } from "../../../server/runtime-config";

vi.mock("~/clients/backend-client.server", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/clients/backend-client.server")>();
  return {
    ...actual,
    backendClient: { ...actual.backendClient, getLibraryCatalog: vi.fn() },
  };
});

beforeEach(() => {
  installFrontendRuntimeConfig({ frontendBackendApiKey: "test-api-key" });
  catalogMock().mockReset();
  catalogMock().mockResolvedValue({
    items: [],
    totalCount: 0,
    page: 1,
    pageSize: 25,
  });
});

function requestFor(path: string): Request {
  return new Request(`http://localhost${path}`);
}

// Wraps the unbound-method lint (vi.mocked unwraps the method from its object).
// Fine here: the mock carries no `this` state.
function catalogMock() {
  // eslint-disable-next-line @typescript-eslint/unbound-method
  return vi.mocked(backendClient.getLibraryCatalog);
}

describe("library loader", () => {
  it("passes search, filter, sort, and pagination to the catalog client", async () => {
    await loader({
      request: requestFor("/library?q=dune&type=broken&sort=size&dir=desc&page=2"),
      params: {},
    } as never);

    expect(catalogMock()).toHaveBeenCalledWith({
      q: "dune",
      type: "broken",
      sort: "size",
      dir: "desc",
      page: 2,
      pageSize: 25,
    });
  });

  it("clamps invalid page and pageSize to defaults", async () => {
    await loader({ request: requestFor("/library?page=0&pageSize=9999"), params: {} } as never);

    expect(catalogMock()).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 25 }));
  });
});
