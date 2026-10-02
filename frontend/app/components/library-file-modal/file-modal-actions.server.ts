import { backendClient } from "~/clients/backend-client.server";

/**
 * Handles the media file modal's form actions (currently `prewarm`). Shared by the
 * Media Library route and the `/library-file` resource route so every page that
 * opens the modal behaves the same.
 */
export async function handleLibraryFileAction(request: Request): Promise<Response> {
  const form = await request.formData();
  const operation = form.get("operation");
  const davItemId = form.get("davItemId");
  if (operation === "prewarm" && typeof davItemId === "string" && davItemId) {
    try {
      await backendClient.warmPrefetch([davItemId]);
      return Response.json({ status: true });
    } catch (error) {
      const message = error instanceof Error ? error.message : "Could not prewarm this file.";
      return Response.json({ status: false, error: message }, { status: 502 });
    }
  }
  return Response.json({ status: false, error: "Unknown library action." }, { status: 400 });
}
