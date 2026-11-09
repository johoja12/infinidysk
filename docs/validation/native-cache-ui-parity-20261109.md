# Native Cache UI parity with the v1 fork

Compared with preserved `origin/v1` commit `9c104b5961abe67c14e6565932e48ea340f1dbd3`. This release restores the Native Cache workflows in the Rust UI. It changes authenticated, read-only administrative queries and presentation. It does not change the cache format, writer, eviction policy, FUSE delivery or database schema.

| v1 workflow | Rust UI |
| --- | --- |
| Overview, Files, Evictions | Separate tabs, with Diagnostics kept separate |
| Storage summary and folders | Original allocation/quota, file/hold counts, folder usage bars, priority, write and eviction state |
| Recent files and active transfers | Recent original catalogue entries and explicitly labelled active warming |
| File filters | Server-side name/ID search, folder, cached/complete/partial/empty/held |
| File ordering and pagination | Recent, name or coverage, stable keyset cursors tied to filter context |
| Coverage map | Exact cached ranges, visual gaps, bounded verification, paged range details |
| Eviction history | Preserved v1 history, search/reason/folder filters and stable pages; distinct current service log |
| Folder settings | Collapsed quota/priority/mode summaries, existing editable fields and environment locks |

Hit rate and compatible bytes added are labelled since restart. Old v1 traffic rows do not provide current rolling bandwidth saved. The active warming list does not claim to include all foreground transfers. Service eviction records contain item/revision identity rather than historical display names/folders; the UI states this. Broader lifecycle work remains separate in #250.

Original metadata was inspected read-only. It contains 10,000 historical eviction records previously hidden by the Rust-only event log. Twenty-six cache rows lacked stored names; all had current imported source labels. Labels are resolved in the query view without updating the original catalogue. Alias resolution is bounded to 2,000 missing labels; additional unresolved entries retain IDs. Original rows with no current source remain browsable without broken source links.

Production catalogue benchmark (read-only SQLite, roughly 31,200 entries): matched count plus 51-row page took 51 ms recent, 50 ms name and 47 ms coverage. These are query timings, not frontend or streaming throughput measurements.

Validation covers generated metadata, original history availability, filters applied before pagination, stable ties/cursors, literal wildcard searches, name resolution without catalogue mutation, current service history filtering, range inspection, protected and unknown holds, read-only controls and environment locks. Browser fixtures use generated names and data; production acceptance is recorded separately after deployment.

Production must retain the four existing cache folders, catalogue/volume ownership markers and disabled eviction. No media payload copy or removal is part of this release. The previous image and verified application snapshot remain available, with fresh configuration and cache metadata backups before replacement.

Local checks: all 66 browser scenarios passed across 360/768/1280 widths in light/dark themes. Generated desktop/mobile Overview, Files, range-map and settings images were reviewed. The Node 24 production build, workspace Clippy with warnings denied, formatting and whitespace checks passed. Full workspace test results and production acceptance follow in the PR release record.
