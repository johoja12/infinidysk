# Media Library [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The **Media Library** page (`/library`) is a read-only catalog of your InfiniDysk media and the library symlinks that point at it.

## Grouped browse [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The page groups recognized `TV`, `TV Shows`, `Shows`, `Series`, `Movies`, and `Films` library paths by show or movie. Paths without a recognizable folder, plus external-only links, stay under **Unmatched**. The page does not infer metadata from filenames or contact Plex.

Expand a group to see its real files. Each file is deduplicated by its stable InfiniDysk identity and keeps every mapping inspectable. Recognizable `S01E02` patterns in file paths appear as episode labels. Links that resolve to InfiniDysk `/.ids/...` targets are marked **internal** and can be opened for streaming; all other symlinks are marked **external** and are inspection-only.

Use the search box to match group names, file names, content paths, symlink paths, or targets. Filter by mapping type (internal / external / broken). Filtering and counts cover the full index before groups are paginated. The header shows when the library index was last scanned; a warning appears when the scan is stale or the library directory is unavailable. Counts describe indexed records only, not Plex sync, cache coverage, or missing episodes.

The grouped browse layout was added without a new setting. The setup wizard needs no change because it already collects the Library Directory for installations that use an organized library.

## File details modal [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

Selecting a row opens a details modal without leaving the page. External items show their mappings only; internal items also offer actions:

- **Preview** plays the file in the built-in player; **Download** saves it directly.
- **Run health check** queues all due health checks — not just this file.
- **Requeue repair** re-queues this file when its latest result needs action.
- **Prewarm** warms this file into the Native cache (hidden with an explanation when Native cache is inactive).

The modal also lists every symlink mapping for the item and its latest health-check result, with a link to **Health** (`/health`) for full history.

The catalog and mapping list are read-only: the modal never edits or deletes library files or symlinks. Health checks, repair requeues, and prewarming schedule backend work without changing the catalog's mappings. Use **Files** (`/explore`) to browse the raw virtual filesystem.
