# Plex Source Accordion Design

**Date:** 2026-09-21  
**Status:** Approved visual direction; awaiting written-spec review  
**Scope:** Smart Prefetch Plex source configuration only

## Problem

The current Plex source configuration exposes the catalogue as a long list of raw
hub and collection records. Each row repeats its Plex kind and media type and uses
an `Add source ...` action. Large catalogues produce duplicate-looking entries,
make Movies and TV sources difficult to distinguish, and force routine choices to
compete with limits, exclusions, previews, users, and refresh metadata.

The redesign should retain InfiniDysk's source-level control while adopting the
clearer NzbDav hierarchy: media type, library accordion, and simple on/off choices.

## Goals

- Separate Movies and TV shows into clear top-level sections.
- Replace add/remove source actions with direct on/off switches.
- Group sources by Plex library and hide detail inside accordions.
- Give a newly enabled library useful, media-specific defaults.
- Keep collections optional, nested, and off by default.
- Preserve source limits, TV exclusions, previews, Plex Home users, refresh
  status, and draft-versus-saved behavior without showing them all at once.
- Work cleanly on desktop and mobile and remain keyboard and screen-reader usable.

## Non-goals

- No backend Plex discovery or persistence contract changes.
- No changes to prefetch scheduling, prediction, cache, or warming behavior.
- No automatic enablement of every hub or collection.
- No automatic rewrite of existing saved source selections.
- No setup-wizard step or wizard-version bump. Plex source selection remains an
  optional Smart Prefetch configuration task, not a new-install critical path.

## Information Architecture

The existing `Plex libraries and sources` disclosure remains under Smart Prefetch.
Inside it, the source configuration has four levels:

1. **Connection and profile bar**
   - Connected Plex server status.
   - Server selector when more than one enabled server exists.
   - Watching-profile selector, defaulting to all users.
   - Refresh action and last successful catalogue refresh.
2. **Media sections**
   - Movies.
   - TV shows.
   - Each section has a master switch and an enabled-library/source summary.
3. **Library accordions**
   - One row per compatible Plex library.
   - Library name, available collection/hub counts, disclosure control, and
     library switch.
4. **Sources and collections**
   - Hub rows use direct switches.
   - Collections live in a nested accordion and use direct switches.
   - Enabled sources expose a secondary `Customize` action.

Unsupported music, photo, and generic clip sources are not shown. A mixed global
hub is placed in Movies or TV only when its resolved media type is supported; it
is never rendered as an unexplained `mixed` entry.

## Switch Behavior

### Media-section master switch

- Turning Movies or TV off disables warming from every source in that section.
- Child selections remain stored so turning the section back on restores them.
- Turning a section on does not enable every library. It restores prior child
  choices; if none exist, the section remains on with zero selected libraries and
  displays an instruction to choose a library.

### Library switch

Turning on a library with no prior source choices selects these defaults:

- **Movie library:** `Recently Added`.
- **TV library:** `On Deck` and `Continue Watching`.

Only matching sources actually returned by Plex are enabled. Missing recommended
sources are skipped without inventing synthetic catalogue entries. If no
recommended source is available, the library opens and asks the user to choose a
source.

Turning a library off disables its selected sources while retaining their choices.
Turning it back on restores those choices rather than reapplying defaults.

### Source switch

- On adds or enables the corresponding persisted `PrefetchSource` draft.
- Off disables the source but preserves its limit and exclusions so they return if
  re-enabled.
- There is no separate Add or Remove button in the normal flow.

### Collection switch

- Collections are always optional and off by default.
- They appear under a nested `Collections` accordion within their owning library.
- Enabling a library or media section never enables collections automatically.

## Catalogue Grouping and Deduplication

The UI derives a view model from the existing library and source snapshots. A
catalogue entry is uniquely identified by:

`serverId + libraryId + kind + key`

Entries with the same identity render once, even if Plex returns duplicates.
Ordering is deterministic:

1. Libraries in Plex response order.
2. Recommended hubs first.
3. Remaining hubs alphabetically by display title.
4. Collections alphabetically by display title.

Saved sources absent from the newest catalogue remain visible in a `Saved but not
currently available` group. They are not silently deleted or disabled. The user
may retain them or turn them off explicitly.

## Profiles and Users

The current server-scoped Plex Home user identities remain intact. The normal UI
uses one `Watching profile` selector:

- `All Plex users` represents no explicit user IDs, preserving current semantics.
- Individual connected users are shown by display name.
- A multi-user selection is available through `Choose profiles...` when needed.

Raw server/user IDs are not shown in the normal view. They may appear in advanced
diagnostics only.

## Customization Drawer

Each enabled source has a `Customize` action that opens an inline drawer containing
only controls relevant to that source:

- Item limit.
- TV show exclusions for show/episode sources.
- Preview action and preview results.
- Exact mapping status and reason when previewed media cannot be warmed.

Closing the drawer keeps draft edits. Disabling a source does not erase them.

## Saving and Refreshing

- All switches and customization controls edit the page's existing Smart Prefetch
  draft state.
- A local `Apply source changes` action uses the same owning Settings save path as
  General Apply; it is not a second persistence API.
- The UI shows an unsaved-change indicator whenever the draft differs from the
  loaded configuration.
- Successful Apply shows a concise confirmation and updates the saved baseline.
- Refresh fetches fresh libraries, users, and sources but never overwrites saved or
  draft selections.
- Refresh is disabled while already running and reports stale snapshots or partial
  Plex errors beside the affected catalogue area.

## Empty and Error States

- **No enabled Plex server:** show `Connect a Plex server first` with a link to the
  Plex server section; do not render empty library accordions.
- **No compatible libraries:** explain that only movie and TV libraries can be
  used for Smart Prefetch.
- **Empty library:** show `No compatible hubs or collections returned by Plex`.
- **Partial refresh failure:** retain the last successful catalogue, mark it stale,
  and show the server-provided error without clearing selections.
- **Unavailable saved source:** retain it in the explicit unavailable group.

## Accessibility and Responsive Behavior

- Accordions use native disclosure semantics or equivalent buttons with
  `aria-expanded` and `aria-controls`.
- Every switch has a unique accessible name containing its section, library, and
  source where needed.
- Section and library summaries are text, not color-only status.
- Focus remains on the triggering control after toggling; opening Customize moves
  focus to its first field only when explicitly requested.
- On narrow screens, Movies and TV become stacked cards. Library accordions remain
  collapsed by default, counts shorten to compact summaries, and all switches
  retain at least a 44-pixel touch target.

## Existing-Configuration Compatibility

- Existing `Users` and `Sources` JSON remain authoritative and load without
  migration.
- Existing enabled/disabled values, limits, and exclusions are preserved exactly.
- Smart defaults apply only when a user turns on a library that has no prior source
  choices.
- Environment-managed `smart-prefetch.settings` remains read-only through the
  existing `ManagedSetting` behavior; the redesigned controls are visibly pinned
  and Apply remains unavailable.

## Component Boundaries

The implementation should split the current large source component into focused
units:

- `PlexSourceConfiguration`: owns loading, refresh, draft integration, and errors.
- `PlexSourceToolbar`: server, profile, refresh, and status.
- `PlexMediaSection`: Movies or TV section-level behavior.
- `PlexLibraryAccordion`: library summary and smart-default activation.
- `PlexSourceToggle`: one hub or collection selection.
- `PlexSourceCustomization`: limits, exclusions, and preview.
- A pure catalogue-grouping helper for classification, deduplication, ordering,
  and unavailable saved sources.

These components consume the existing Plex API types and `PrefetchSettings`; no
new backend endpoint is required.

## Test Strategy

Focused frontend tests must cover:

- Movies and TV render as separate sections; music/photo/unsupported clips do not.
- Duplicate source identities render once.
- Enabling a new movie library selects only Recently Added.
- Enabling a new TV library selects only On Deck and Continue Watching.
- Missing recommended hubs produce an actionable empty selection.
- Re-enabling a library restores previous choices rather than resetting them.
- Section-off retains child choices and section-on restores them.
- Collections remain off when a library is enabled and can be toggled individually.
- Existing source limits and exclusions survive disable/re-enable and refresh.
- Refresh preserves draft and saved selections, including unavailable sources.
- Environment-managed settings stay read-only.
- Accordions and switches expose correct accessible names and expanded state.
- The compact mobile layout retains all controls and touch targets.

Backend tests are unnecessary unless implementation reveals that the existing API
cannot supply a stable library/source identity or resolved media type. Any such
contract change requires a separate design review before expanding scope.

## Acceptance Criteria

- A user can configure ordinary movie and TV prefetch sources without seeing raw
  Plex kinds, IDs, or repeated `Add source` actions.
- Movies and TV are visually and semantically separate.
- One library switch establishes the approved smart defaults.
- Collections are discoverable but optional and off by default.
- Advanced controls remain available without cluttering the default layout.
- Existing configurations round-trip without data loss or silent rewrites.
- Desktop and mobile layouts meet the stated accessibility behavior.
