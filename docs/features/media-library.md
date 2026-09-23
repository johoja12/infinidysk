# Media Library [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The **Media Library** page (`/library`) is a read-only catalog of your InfiniDysk media and the library symlinks that point at it.

Each file is one media item (deduplicated by its stable InfiniDysk identity) with its symlink mappings nested underneath. Links that resolve to InfiniDysk `/.ids/...` targets are marked **internal** and can be opened for streaming; all other symlinks are marked **external** and are inspection-only.

## Grouped browsing [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

The **TV shows** and **Movies** tabs classify files by matching their symlink filename against metadata from enabled Plex servers. A Plex episode match goes under TV shows; a Plex movie match goes under Movies. Recognized library folders, including `TV-*` and `Movies-*`, organize matched files into groups. If the folder does not match the Plex media type, the Plex show or movie title supplies the group. Expand a group to see its files and their mappings without leaving the page; large groups have their own inline file pages. The **Unmatched** tab lists files with no unambiguous Plex filename match, including external-only links when Plex does not recognize their filenames.

Search matches file names, content paths, symlink paths, and targets across the full indexed catalog. The mapping filter includes internal, external, and broken links. Quality filters use resolution tags in filenames (4K, 1080p, 720p, or SD); files without a recognized tag are **Unknown**. Stored cache filters use Native Cache verified bytes for an item. This is storage coverage and may include an older file revision, so it does not guarantee that a stream will hit the current cache. When Native Cache is inactive, coverage is **Unavailable**. Summary cards count indexed files, internal files with valid mappings, files needing attention, and unmatched files for the current search and mapping filter. The scan timestamp and any stale-index warning appear above the groups.

The Plex index syncs shortly after startup and every six hours. **Sync Plex now** refreshes it on demand. The page shows the last successful sync, the number of indexed Plex items, and any sync warning. A failed or incomplete Plex request keeps the last complete index; before the first successful sync, the page leaves categories pending rather than treating every file as unmatched. The index is saved in `/config/plex-library-metadata.json`. Plex matching uses filenames, so files renamed differently from their Plex versions may remain unmatched. The page does not provide missing-episode detection or language analysis.

## File details modal [since 1.5.0](https://github.com/infinidysk/infinidysk/releases/tag/v1.5.0){ .nzbdav-since }

Selecting a file opens a details modal without leaving the page. External items show their mappings only; internal items also offer actions:

- **Preview** plays the file in the built-in player; **Download** saves it directly.
- **Run health check** queues all due health checks — not just this file.
- **Requeue repair** re-queues this file when its latest result needs action.
- **Prewarm** warms this file into the Native cache (hidden with an explanation when Native cache is inactive).

The modal also lists every symlink mapping for the item and its latest health-check result, with a link to **Health** (`/health`) for full history.

The catalog and mapping list are read-only: the modal never edits or deletes library files or symlinks. Health checks, repair requeues, and prewarming schedule backend work without changing the catalog's mappings. Use **Files** (`/explore`) to browse the raw virtual filesystem.
