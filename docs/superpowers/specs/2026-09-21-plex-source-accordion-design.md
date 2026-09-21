# Plex Source Accordion Design

**Date:** 2026-09-21
**Status:** Approved
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

- No Plex discovery endpoint or database-schema changes.
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

A library switch is on when the library has at least one persisted source choice and
its identity is not in `DisabledLibraries`. A library with no persisted source choice
is off, including on existing configurations where `DisabledLibraries` is absent.

Turning on a library with no prior source choices selects these defaults:

- **Movie library:** `Recently Added`.
- **TV library:** `On Deck` and `Continue Watching`.

Only matching sources actually returned by Plex are enabled. Missing recommended
sources are skipped without inventing synthetic catalogue entries. If no
recommended source is available, the library opens and asks the user to choose a
source; the switch remains off until that choice creates the first persisted source.

Turning a library off adds its stable identity to `DisabledLibraries`; it does not
change the enabled state, limit, or exclusions of any child source. Turning it back
on removes that identity and restores those choices rather than reapplying defaults.

`DisabledLibraries` is a bounded array of objects containing `ServerId`,
`LibraryId`, and normalized `Type` (`movie` or `show`). The media type distinguishes
movie and TV global-hub groups whose `LibraryId` is empty. Existing configurations
default to an empty array. The prefetch scheduler skips a source when its normalized
library identity appears in this array.

### Source switch

- On adds or enables the corresponding persisted `PrefetchSource` draft.
- Enabling the first source in a disabled library also removes that library identity
  from `DisabledLibraries`.
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

## Configuration Contract and Compatibility

- Existing `Users` and `Sources` JSON remain authoritative and load without a data
  migration. The new optional `DisabledLibraries` array defaults to empty.
- Existing enabled/disabled values, limits, and exclusions are preserved exactly.
- Smart defaults apply only when a user turns on a library that has no prior source
  choices.
- Library switches update only `DisabledLibraries`; they never rewrite child source
  `Enabled` values. Source switches continue to own those values.
- The backend validates at most 128 unique library identities, bounded string
  lengths, and only normalized `movie`/`show` types. Duplicate or malformed entries
  reject the settings update.
- Scheduling filters disabled library identities after source/server matching and
  before any Plex source request, so disabling a library causes no catalogue fetch
  or warming work for its sources.
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

These components consume the existing Plex API types. `PrefetchSettings` gains the
backward-compatible `DisabledLibraries` field; no new endpoint or database migration
is required.

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

Focused backend tests must cover:

- Missing `DisabledLibraries` parses as an empty array.
- Valid library identities round-trip and malformed, duplicate, or over-limit
  identities are rejected.
- A disabled library makes no Plex source request and queues no warming job.
- Re-enabling the library resumes its previously enabled sources without rewriting
  their settings.

Because this is an optional property inside the existing validated JSON setting, it
does not require an EF migration. The setup wizard remains unchanged: Plex source
selection is still advanced and optional, and the field defaults safely for new and
existing installations.

## Acceptance Criteria

- A user can configure ordinary movie and TV prefetch sources without seeing raw
  Plex kinds, IDs, or repeated `Add source` actions.
- Movies and TV are visually and semantically separate.
- One library switch establishes the approved smart defaults.
- Library on/off state persists independently without losing child source choices.
- Collections are discoverable but optional and off by default.
- Advanced controls remain available without cluttering the default layout.
- Existing configurations round-trip without data loss or silent rewrites.
- Desktop and mobile layouts meet the stated accessibility behavior.
