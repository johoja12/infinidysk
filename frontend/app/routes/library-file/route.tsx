import type { Route } from "./+types/route";
import { BackendApiError, backendClient } from "~/clients/backend-client.server";
import { getPreviewUrl } from "~/auth/downloads.server";
import { getFrontendRuntimeConfig } from "../../../server/runtime-config";
import { handleLibraryFileAction } from "~/components/library-file-modal/file-modal-actions.server";
import type { LibraryFileLookup } from "~/components/library-file-modal/use-library-file-modal";

const davItemIdPattern = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

// Resource route behind the shared media file modal for pages that only know a DavItem id
// (Smart Prefetch). It returns library details plus a signed preview URL, or a reason the
// file has no Media Library record so the modal can degrade instead of failing.
export async function loader({ request }: Route.LoaderArgs) {
  const davItemId = new URL(request.url).searchParams.get("davItemId")?.trim() ?? "";
  if (!davItemIdPattern.test(davItemId))
    return Response.json({ error: "Invalid davItemId." }, { status: 400 });

  let details;
  try {
    details = await backendClient.getLibraryFileDetails(davItemId);
  } catch (error) {
    if (!(error instanceof BackendApiError) || error.status !== 404) throw error;
    const disabled = /disabled/i.test(error.message);
    return Response.json({
      details: null,
      previewUrl: null,
      libraryRoot: null,
      unavailableReason: disabled
        ? "Media Library is disabled, so library details and repair actions are unavailable."
        : "This file is not in the Media Library, so library details and repair actions are unavailable.",
    } satisfies LibraryFileLookup);
  }

  let libraryRoot: string | null = null;
  try {
    const settings = await backendClient.getConfig(["media.library-dir"]);
    libraryRoot =
      settings.find((item) => item.configName === "media.library-dir")?.configValue.trim() || null;
  } catch {
    // Mapping paths fall back to their relative form.
  }
  const { frontendBackendApiKey } = getFrontendRuntimeConfig();
  return Response.json({
    details,
    previewUrl: details.contentPath
      ? getPreviewUrl(details.contentPath, frontendBackendApiKey)
      : null,
    libraryRoot,
    unavailableReason: null,
  } satisfies LibraryFileLookup);
}

export async function action({ request }: Route.ActionArgs) {
  return await handleLibraryFileAction(request);
}
