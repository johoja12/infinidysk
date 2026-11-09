# Fork feature UI qualification

The 2026-11-07 inventory counted backend outcomes and existing components as
implemented. That did not establish complete, discoverable UI workflows. The
production report on 2026-11-09 exposed the missing navigation and original-cache
administration surface. This change corrects those gaps for native cache, Plex
library browsing and Smart Prefetch. Broader reconciliation/lifecycle work remains
tracked separately in #250; this is not a claim of complete upstream-v1 parity.

## Operator workflows

- `/native-cache`: original-store folder totals, accounted/current/pending/retired
  bytes, eviction holds, cache file search and cursor paging, exact-entry ranges,
  bounded hash verification, manual pins and guarded removal. Missing imported
  sources remain inspectable without linking to a nonexistent file. Storage
  identity, free space and write permissions are checked on request. Unknown
  recency, active readers, pending publication and health/unknown pins retain
  their eviction protection. Policy remains opt-in per folder.
- `/settings/native-cache`: shared-cache configuration and existing-format folder
  settings, quotas, reserves and priorities. Environment-managed values show
  their lock reason. Storage changes still require restart. Existing-format
  storage remains in place; browsing does not copy or convert payloads.
- `/plex-library`: configured servers and movie/TV libraries, bounded 50-record
  search/pages, show-to-episode navigation, exact imported-source links, cache
  inspection and manual warming. Multiple distinct media versions are not guessed.
  Connection and path mapping management is available on the same page.
- `/smart-prefetch`: separate activity/history, predictions and settings views.
  Settings discover named hubs, collections and whole libraries using an explicit
  server/profile context. Sources support limits, exclusions and metadata-only
  previews. Saved labels/selections survive discovery failure. One Save/Discard
  bar preserves concurrent edits to untouched settings and warns before navigation.
  Advanced policy stays collapsed initially.

Legacy cache administration uses the already owned store. It does not reopen
ownership locks or adopt a volume. Manual removal shares the existing durable
policy-deletion protocol and requires explicit eviction permission. Unpin removes
manual pin 1 only; health pin 2 and unknown holds remain protected. A verification
request reads at most one committed block and does not contact providers.

Plex history events without a media identity are counted and excluded from
prediction inputs, with a visible warning. Actionable metadata and viewer access
remain strictly validated. The request/page/memory limits are unchanged.

## Evidence

The browser suite uses generated data, including an original cache with zero
new-store totals, protected pins, unavailable saved sources and concurrent settings
changes. It exercises five workflows at 360, 768 and 1280 pixels in both themes.
Screenshots are generated fixtures, not production acceptance.

A private read-only probe of the configured production Plex server verified server
and viewer identity, 18 Home sources, 104 sources in one movie library and 36 in
one TV library, plus bounded 50-record browsing in both library types. The newest
1,000 history events included 992 usable records and eight without media identity.
The probe made no provider requests and no production database/cache writes.

Final local/CI results and exact deployed source/image identity are recorded in the
PR and production acceptance receipt after their checks complete. Live activation
is distinct from fixture evidence. Cache byte checks are bounded canaries rather
than an exhaustive scrub of the existing media store.
