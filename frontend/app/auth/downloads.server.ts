import { createHmac } from "node:crypto";

export function getDownloadKey(path: string, frontendBackendApiKey: string): string {
  return createHmac("sha256", frontendBackendApiKey).update(path).digest("hex");
}

/** Signed `/view` URL that streams an imported file's content path in the browser. */
export function getPreviewUrl(contentPath: string, frontendBackendApiKey: string): string {
  const relative = contentPath.startsWith("/") ? contentPath.slice(1) : contentPath;
  return `/view${contentPath}?downloadKey=${getDownloadKey(relative, frontendBackendApiKey)}`;
}
