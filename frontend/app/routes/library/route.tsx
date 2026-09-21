import type { Route } from "./+types/route";
import { Form, Link, useSearchParams } from "react-router";
import {
  backendClient,
  type LibraryCatalogItem,
  type LibraryCatalogResponse,
} from "~/clients/backend-client.server";
import { getDownloadKey } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../../server/runtime-config";
import { formatFileSize } from "~/utils/file-size";
import { Badge, Button, Input, PageHeader } from "~/components/ui";

const PAGE_SIZE_OPTIONS = [25, 50, 100] as const;
const DEFAULT_PAGE_SIZE = 25;

export type LibraryPageData = {
  query: { q: string; type: string; sort: string; dir: string; page: number; pageSize: number };
  catalog: LibraryCatalogResponse;
  downloadKeys: Record<string, string>;
};

function parseType(value: string | null) {
  return value === "internal" || value === "external" || value === "broken" ? value : "all";
}

function parseSort(value: string | null) {
  return value === "size" || value === "mappings" ? value : "name";
}

function parseDir(value: string | null) {
  return value === "desc" ? "desc" : "asc";
}

function parsePage(value: string | null): number {
  const page = parseInt(value ?? "1", 10);
  return Number.isFinite(page) && page > 0 ? page : 1;
}

function parsePageSize(value: string | null): number {
  const size = parseInt(value ?? String(DEFAULT_PAGE_SIZE), 10);
  return (PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : DEFAULT_PAGE_SIZE;
}

export async function loader({ request }: Route.LoaderArgs): Promise<LibraryPageData> {
  const url = new URL(request.url);
  const query = {
    q: url.searchParams.get("q")?.trim() ?? "",
    type: parseType(url.searchParams.get("type")),
    sort: parseSort(url.searchParams.get("sort")),
    dir: parseDir(url.searchParams.get("dir")),
    page: parsePage(url.searchParams.get("page")),
    pageSize: parsePageSize(url.searchParams.get("pageSize")),
  };
  const catalog = await backendClient.getLibraryCatalog({
    ...(query.q ? { q: query.q } : {}),
    type: query.type as "all" | "internal" | "external" | "broken",
    sort: query.sort as "name" | "size" | "mappings",
    dir: query.dir as "asc" | "desc",
    page: query.page,
    pageSize: query.pageSize,
  });
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  const downloadKeys: Record<string, string> = {};
  for (const item of catalog.items) {
    if (item.kind === "internal" && item.contentPath && item.davItemId) {
      const relative = item.contentPath.startsWith("/") ? item.contentPath.slice(1) : item.contentPath;
      downloadKeys[item.davItemId] = getDownloadKey(relative, frontendBackendApiKey);
    }
  }
  return { query, catalog, downloadKeys };
}

export default function Library({ loaderData }: Route.ComponentProps) {
  const [searchParams] = useSearchParams();
  const { query, catalog, downloadKeys } = loaderData;
  const totalPages = Math.max(1, Math.ceil(catalog.totalCount / catalog.pageSize));

  return (
    <div className="mx-auto flex w-full max-w-6xl flex-col gap-6 px-4 py-8 md:px-6">
      <PageHeader
        title="Media Library"
        subtitle="Read-only catalog of InfiniDysk media and its library symlinks."
      />
      {catalog.indexWarning ? (
        <div className="alert alert-warning">
          <span>Library index may be stale: {catalog.indexWarning}</span>
        </div>
      ) : null}
      <Form method="get" className="flex flex-wrap gap-2">
        <Input name="q" defaultValue={query.q} placeholder="Search title, content path, symlink…" />
        <select name="type" defaultValue={query.type} className="select select-bordered" aria-label="Mapping filter">
          <option value="all">All mappings</option>
          <option value="internal">Internal</option>
          <option value="external">External</option>
          <option value="broken">Broken</option>
        </select>
        <select name="sort" defaultValue={query.sort} className="select select-bordered" aria-label="Sort">
          <option value="name">Name A–Z</option>
          <option value="size">Size</option>
          <option value="mappings">Mappings</option>
        </select>
        <Button type="submit">Search</Button>
      </Form>
      <p className="text-sm text-base-content/60">
        {catalog.totalCount} items · page {catalog.page} of {totalPages}
        {catalog.indexScannedAt ? ` · index fresh as of ${catalog.indexScannedAt}` : null}
      </p>
      <div className="overflow-x-auto">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Location</th>
              <th>Size</th>
              <th>Mappings</th>
              <th>State</th>
            </tr>
          </thead>
          <tbody>
            {catalog.items.map((item) => (
              <CatalogRow key={item.davItemId ?? item.displayName} item={item} downloadKeys={downloadKeys} search={searchParams.toString()} />
            ))}
          </tbody>
        </table>
      </div>
      <nav className="join" aria-label="Pagination">
        {query.page > 1 ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page - 1)}`}>Previous</Link>
        ) : null}
        {query.page < totalPages ? (
          <Link className="btn join-item" to={`?${withPage(searchParams, query.page + 1)}`}>Next</Link>
        ) : null}
      </nav>
    </div>
  );
}

function withPage(params: URLSearchParams, page: number): string {
  const next = new URLSearchParams(params);
  next.set("page", String(page));
  return next.toString();
}

function CatalogRow({
  item,
  downloadKeys,
  search,
}: {
  item: LibraryCatalogItem;
  downloadKeys: Record<string, string>;
  search: string;
}) {
  return (
    <>
      <tr>
        <td>{item.displayName}</td>
        <td className="max-w-xs truncate">{item.contentPath ?? item.mappings[0]?.targetText ?? "—"}</td>
        <td>{item.size != null ? formatFileSize(item.size) : "—"}</td>
        <td>{item.mappingCount}</td>
        <td>
          <Badge>{item.health}</Badge>
        </td>
      </tr>
      <tr>
        <td colSpan={5}>
          <details>
            <summary>{item.mappingCount} mapping(s) — expand to inspect</summary>
            <ul className="mt-2 flex flex-col gap-1">
              {item.mappings.map((m) => (
                <li key={m.linkPath} className="flex flex-wrap items-center gap-2 text-sm">
                  <Badge>{m.mappingType}</Badge>
                  <Badge>{m.status}</Badge>
                  <code className="break-all">{m.linkPath} → {m.targetText}</code>
                  {m.mappingType === "internal" && item.davItemId && item.contentPath ? (
                    <Link
                      className="link"
                      to={`/view${item.contentPath}?downloadKey=${downloadKeys[item.davItemId] ?? ""}&${search}`}
                    >
                      Open
                    </Link>
                  ) : (
                    <span className="text-base-content/50">inspection only</span>
                  )}
                </li>
              ))}
            </ul>
          </details>
        </td>
      </tr>
    </>
  );
}
