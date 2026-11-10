# v1 UI parity follow-ups

Issue: #263. V1 is a read-only specification (`origin/v1`, `9c104b59`).

## Changes

- Plex Library has summary counts, TV/Movie/All/Unmatched tabs, grouped cards and file tables, path search, mapping/match/quality/cache/watch/season filters, stable paging, and file inspection with existing playback, warming and cache tools. Complete metadata snapshots are filtered before paging. Different native pointer paths remain distinct even when they reference the same item.
- Library cache links accept both compact v1 and hyphenated v2 item IDs. Name searches retain literal wildcard escaping.
- Cache coverage is a bounded background metadata pass. Candidate identities are not coverage proof. Current source keys are checked before displaying existing coverage; unopened stores and failed checks remain unknown. Browsing does not download media. Playback and warming revalidate the exact Plex Part or native pointer immediately before acting.
- Smart Prefetch records the verified viewer name and selected source title/path. Multiple hubs for the same media retain separate provenance. Old JSON remains readable. A display-only registry can recover historical labels for an exact policy scope; this does not rewrite the scheduling journal or history payloads.
- Saved sources have explicit availability checks using the selected viewer credential. Disabled selections are probed on a request-local copy. Checks do not enable selections, replace paths, queue jobs or download media.
- Health exposes Quick, Standard, Enhanced, Deep, Complete and custom percentage. Standard is the v1 size-based curve with an 8,000-article floor and edge sampling; it is not a fixed percentage. Core sampling is compared with the v1 algorithm. Routine checks and automatic replacement default on. Automatic media analysis and PAR2 default off. Existing explicit environment locks remain supported.

## Bounds and freshness

The browser retains at most two complete snapshots, with one metadata builder and one coverage worker. Snapshots expire five minutes after coverage completes. Each snapshot is limited to 150,000 rows and 128 MB of serialized metadata. Per-library Plex catalogue fetches retain the existing 100,000-item transport bound. A failed refresh retains previous results and reports a warning. Coverage filters wait for the whole pass. At most 32 unindexed local paths are probed during a build; other paths remain unknown until the native index is refreshed. Inspection and actions always revalidate live state.

## Qualification

The committed browser fixture contains generated data only. It checks the Library group/table workflows, a match beyond page one, season and cache filters, missing source action restrictions, verified labels, source availability, existing Prefetch settings/history, and original cache workflows at 360, 768 and 1280 pixels in light and dark themes. Backend tests cover legacy sampling, backwards-compatible labels, metadata query batches, exact Plex Part selection, artwork resource validation and complete-catalogue filtering/grouping.

Production acceptance is separate from fixture and CI results. The guarded rollout must verify the exact image/source, original cache canaries, retained cache policy, consumer mounts and public health. It removes only the four migration-only Health overrides and persists the requested Health defaults through the settings API, preserving the existing `action_needed` policy for unowned files. Historical labels are installed only after exact retained scope reconstruction and renewed credential identity verification. The nine unavailable saved Plex paths remain disabled. No existing media payload is copied or removed; cache formats, writer behavior and eviction policy are unchanged.

Local checks completed: `cargo fmt --all --check`; strict workspace Clippy; the full workspace suite (847 passed, 12 ignored); the final focused Health mode-switch regression; the Node 24 UI production build; and all 72 browser checks. Generated desktop and mobile group/table screenshots were inspected. Ignored external-storage tests are not claimed as production qualification.
