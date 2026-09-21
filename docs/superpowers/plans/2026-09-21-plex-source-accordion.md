# Plex Source Accordion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the raw Plex source list with separate Movies and TV library accordions, persistent library/source switches, smart first-enable defaults, optional collections, and a scoped Apply action.

**Architecture:** Extend the existing `smart-prefetch.settings` JSON with a bounded `DisabledLibraries` identity array. The backend scheduler treats that array as a library-level gate without mutating child sources. The frontend derives a deterministic catalogue view model from Plex snapshots and saved sources, keeps all edits in the existing settings draft, and persists only the Smart Prefetch key through the existing Settings save path.

**Tech Stack:** .NET 10/C# records and xUnit; React 19, TypeScript 6, React Router 8, Tailwind/DaisyUI, Vitest, and Testing Library.

---

## File and Responsibility Map

- Modify `backend/Services/Prefetch/PrefetchSettings.cs`: add and validate `DisabledLibraries`, normalize source media identity, and expose the scheduler predicate.
- Modify `backend/Services/Prefetch/PlexPrefetchService.cs`: skip a source before its Plex request when its library identity is disabled.
- Modify `tests/NzbWebDAV.Tests/Services/PrefetchSettingsTests.cs`: cover defaulting, round-trip, duplicates, invalid values, and bounds.
- Modify `tests/NzbWebDAV.Tests/Services/PlexPolicyIntegrationTests.cs`: prove disabled libraries issue no source fetch and re-enabled libraries resume unchanged sources.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts`: mirror the new structured field and validation.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts`: cover backward compatibility and frontend validation.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.ts`: pure classification, deduplication, ordering, recommendation, and unavailable-source helpers.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.test.ts`: unit-test the derived catalogue.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-selection.ts`: pure section/library/source draft mutations.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-selection.test.ts`: unit-test defaults and retention semantics.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-toolbar.tsx`: server, watching-profile, refresh, and freshness controls.
- Create `frontend/app/routes/settings/smart-prefetch/plex-media-section.tsx`: media card, library accordions, nested collections, and accessible switches.
- Create `frontend/app/routes/settings/smart-prefetch/plex-source-customization.tsx`: limit, exclusions, preview, and mapping feedback.
- Modify `frontend/app/routes/settings/smart-prefetch/plex-sources.tsx`: reduce it to snapshot orchestration, catalogue composition, draft mutation, and error handling.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`: compute source-specific dirty state and render scoped Apply feedback.
- Modify `frontend/app/routes/settings/streaming/streaming.tsx`: pass saved config and the scoped persistence callback through.
- Modify `frontend/app/routes/settings/route.tsx`: supply the existing `persistConfigPatch` callback to Streaming settings.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`: exercise the complete accordion flow and local Apply behavior.
- Modify `frontend/app/routes/settings/plex/plex.component.test.tsx`: retain saved-server synchronization coverage against the redesigned component.
- Modify `frontend/app/routes/settings/streaming/streaming.test.ts`: update the Streaming harness for the new persistence props.
- Modify `docs/configuration/native-cache-prefetch.md`: document the simple accordion flow, defaults, retention behavior, and advanced-only setup-wizard decision.

## Task 1: Add the Backward-Compatible Disabled-Library Contract

**Files:**

- Modify: `backend/Services/Prefetch/PrefetchSettings.cs`
- Test: `tests/NzbWebDAV.Tests/Services/PrefetchSettingsTests.cs`

- [ ] Create the implementation branch from the approved design branch before changing product code:

```bash
git switch -c feat/plex-source-accordions
```

- [ ] Add failing parsing and validation tests to `PrefetchSettingsTests.cs`:

```csharp
[Fact]
public void DisabledLibraries_DefaultEmptyAndRoundTripStableIdentity()
{
    Assert.Empty(PrefetchSettings.Parse(null).DisabledLibraries);
    var settings = PrefetchSettings.Parse("""{"DisabledLibraries":[{"ServerId":"plex","LibraryId":"2","Type":"show"}]}""");
    Assert.Equal(new PrefetchLibraryIdentity("plex", "2", "show"), Assert.Single(settings.DisabledLibraries));
}

[Theory]
[InlineData("""{"DisabledLibraries":null}""")]
[InlineData("""{"DisabledLibraries":[{"ServerId":"","LibraryId":"2","Type":"show"}]}""")]
[InlineData("""{"DisabledLibraries":[{"ServerId":"plex","LibraryId":"2","Type":"episode"}]}""")]
[InlineData("""{"DisabledLibraries":[{"ServerId":"plex","LibraryId":"2","Type":"show"},{"ServerId":"plex","LibraryId":"2","Type":"show"}]}""")]
public void InvalidDisabledLibraryIdentities_AreRejected(string json) =>
    Assert.Throws<ArgumentException>(() => PrefetchSettings.Parse(json));
```

- [ ] Run the focused tests and confirm RED because the type/property do not exist:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter FullyQualifiedName~PrefetchSettingsTests
```

Expected: compilation fails on `PrefetchLibraryIdentity` and `DisabledLibraries`.

- [ ] Add the immutable identity record immediately above `PrefetchSettings`:

```csharp
public sealed record PrefetchLibraryIdentity(string ServerId = "", string LibraryId = "", string Type = "movie");
```

- [ ] Add `public PrefetchLibraryIdentity[] DisabledLibraries { get; init; } = [];` beside `Users` and `Sources`.

- [ ] Extend `Parse` validation to require a non-null array of at most 128 entries; require nonblank `ServerId` up to 128 characters, `LibraryId` up to 128 characters, and `Type` equal to `movie` or `show`; reject duplicate `ServerId + LibraryId + Type` identities with ordinal comparison.

- [ ] Add the 129-entry bound case to the test and rerun the focused command.

Expected: all `PrefetchSettingsTests` pass.

- [ ] Commit:

```bash
git add backend/Services/Prefetch/PrefetchSettings.cs tests/NzbWebDAV.Tests/Services/PrefetchSettingsTests.cs
git commit -m "feat(prefetch): persist disabled Plex libraries"
```

## Task 2: Enforce Disabled Libraries Before Plex Source Fetches

**Files:**

- Modify: `backend/Services/Prefetch/PrefetchSettings.cs`
- Modify: `backend/Services/Prefetch/PlexPrefetchService.cs`
- Test: `tests/NzbWebDAV.Tests/Services/PlexPolicyIntegrationTests.cs`

- [ ] Add a helper test showing that movie sources normalize to `movie`, while show, episode, and legacy clip sources normalize to the TV `show` gate:

```csharp
[Theory]
[InlineData("movie", "movie")]
[InlineData("show", "show")]
[InlineData("episode", "show")]
[InlineData("clip", "show")]
public void SourceLibraryType_NormalizesSchedulerIdentity(string sourceType, string expected) =>
    Assert.Equal(expected, PrefetchSettings.LibraryType(new PrefetchSource { Type = sourceType }));
```

- [ ] Add an integration test that configures one enabled source and the matching disabled identity, calls `SyncAsync(true, ...)`, and asserts the fake Plex handler received no `/hubs/source` request and the warming queue stayed empty.

- [ ] Extend that test by saving the same settings with `DisabledLibraries = []`, calling a fresh policy sync, and asserting the unchanged source is fetched and queues the mapped item. This proves re-enabling does not rewrite the child source.

- [ ] Run the focused tests and confirm RED:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PrefetchSettingsTests|FullyQualifiedName~PlexPolicyIntegrationTests"
```

Expected: the new helper is absent and the source request still occurs.

- [ ] Implement the shared normalization and predicate in `PrefetchSettings.cs`:

```csharp
public static string LibraryType(PrefetchSource source) => source.Type == "movie" ? "movie" : "show";

public bool IsLibraryDisabled(PrefetchSource source) => DisabledLibraries.Any(library =>
    library.ServerId == source.ServerId && library.LibraryId == source.LibraryId
    && library.Type == LibraryType(source));
```

- [ ] Change the source loop predicate in `PlexPrefetchService.SyncServerAsync` to include `!settings.IsLibraryDisabled(source)` before any `GetPreviewAsync` call.

- [ ] Rerun the focused backend tests.

Expected: all selected tests pass and the disabled case has zero source fetches.

- [ ] Commit:

```bash
git add backend/Services/Prefetch/PrefetchSettings.cs backend/Services/Prefetch/PlexPrefetchService.cs \
  tests/NzbWebDAV.Tests/Services/PrefetchSettingsTests.cs tests/NzbWebDAV.Tests/Services/PlexPolicyIntegrationTests.cs
git commit -m "fix(prefetch): honor disabled Plex libraries"
```

## Task 3: Mirror the Contract in the Frontend Model

**Files:**

- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts`
- Test: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts`

- [ ] Add frontend model tests for omitted-field defaulting, a valid movie/show identity round-trip, duplicate rejection, invalid type rejection, and the 128-entry bound.

- [ ] Run the focused test and confirm RED:

```bash
cd frontend
npm test -- app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts
```

Expected: assertions fail because `DisabledLibraries` is not normalized or validated.

- [ ] Add the type and extend `PrefetchSettings`:

```ts
export type DisabledPlexLibrary = {
  ServerId: string;
  LibraryId: string;
  Type: "movie" | "show";
};

export type PrefetchSettings = typeof numericDefaults &
  typeof booleanDefaults & {
    Users: string[];
    Sources: PrefetchSource[];
    DisabledLibraries: DisabledPlexLibrary[];
  };
```

- [ ] Add `DisabledLibraries: []` to parsing defaults, normalize its known fields case-insensitively, and validate the same bounds and uniqueness as the backend.

- [ ] Keep `resetPrefetchPolicyDefaults` unchanged apart from type fixes: its existing `{ ...settings, ...defaults }` shape must preserve `Users`, `Sources`, and `DisabledLibraries`.

- [ ] Rerun the focused model test.

Expected: all model tests pass, including old JSON without the new field.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts
git commit -m "feat(ui): model disabled Plex libraries"
```

## Task 4: Build the Deterministic Catalogue View Model

**Files:**

- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.ts`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.test.ts`

- [ ] Write tests with two movie libraries, one TV library, duplicate hubs, collections, unsupported music/photo/clip sources, global hubs with an empty library ID, and a saved source missing from the latest snapshot.

- [ ] Assert the tests cover these exact rules:

  - Movies and TV are separate.
  - Libraries retain Plex response order.
  - Catalogue duplicates by `serverId + libraryId + kind + key` render once.
  - Persisted matching still uses `serverId + kind + key`, matching backend uniqueness.
  - Recommended hubs precede other hubs; remaining hubs and collections sort by title.
  - Global movie and TV groups have distinct normalized library identities.
  - A library is on only when it has at least one persisted source and is absent from `DisabledLibraries`; an unconfigured library is off.
  - Missing saved sources appear under `unavailable` and remain editable.
  - Unsupported sources never appear.

- [ ] Run the new test and confirm RED because the module is absent:

```bash
cd frontend
npm test -- app/routes/settings/smart-prefetch/plex-source-catalogue.test.ts
```

- [ ] Implement exported pure helpers and types:

```ts
export type PlexMediaSection = "movie" | "show";
export type PlexLibraryIdentity = { serverId: string; libraryId: string; type: PlexMediaSection };
export type PlexCatalogueLibrary = {
  identity: PlexLibraryIdentity;
  title: string;
  hubs: PlexSource[];
  collections: PlexSource[];
  unavailable: PrefetchSource[];
};

export function mediaSection(type: string): PlexMediaSection | null;
export function catalogueSourceKey(source: PlexSource): string;
export function persistedSourceKey(serverId: string, kind: string, key: string): string;
export function buildPlexSourceCatalogue(...): { movie: PlexCatalogueLibrary[]; show: PlexCatalogueLibrary[] };
```

- [ ] Detect recommendation semantics from normalized `id + key + title`, preferring stable Plex identifiers/keys and using the English title only as a fallback. Return semantic tags (`recently-added`, `on-deck`, `continue-watching`, or `null`) rather than comparing presentation strings in components.

- [ ] Use a labelled global group such as `More from Plex`; use normalized `movie`/`show` in its identity so an empty `libraryId` does not collide across sections.

- [ ] Rerun the catalogue tests.

Expected: all catalogue tests pass without React or network mocks.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.ts \
  frontend/app/routes/settings/smart-prefetch/plex-source-catalogue.test.ts
git commit -m "feat(ui): derive Plex source catalogue"
```

## Task 5: Implement Pure Selection Mutations

**Files:**

- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-selection.ts`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-selection.test.ts`

- [ ] Write failing tests for section, library, hub, and collection operations:

  - Section off/on changes only `MovieEnabled` or `TvEnabled` and retains all child fields.
  - Library off adds exactly one normalized identity to `DisabledLibraries` and does not touch `Sources`.
  - Library on removes the identity and preserves existing source `Enabled`, `Limit`, and `ExcludedShows` values.
  - First enabling an unconfigured movie library adds and enables only its `recently-added` hub.
  - First enabling an unconfigured TV library adds and enables only its `on-deck` and `continue-watching` hubs.
  - Missing recommendations add nothing and return a result flag that tells the UI to open the library and show guidance.
  - A missing-recommendation library remains off until the user enables its first source.
  - Collections are never selected by defaults.
  - Source off retains the source object and customization while changing only `Enabled`.
  - Source on creates a bounded default source when absent and re-enables the existing object when present.
  - Source on removes the owning identity from `DisabledLibraries` so direct child interaction cannot leave the source scheduler-blocked.

- [ ] Run the test and confirm RED:

```bash
cd frontend
npm test -- app/routes/settings/smart-prefetch/plex-source-selection.test.ts
```

- [ ] Implement pure functions returning new settings objects, never mutating their arguments:

```ts
export function setMediaSectionEnabled(
  settings: PrefetchSettings,
  type: PlexMediaSection,
  enabled: boolean,
): PrefetchSettings;
export function setLibraryEnabled(
  settings: PrefetchSettings,
  library: PlexCatalogueLibrary,
  enabled: boolean,
): {
  settings: PrefetchSettings;
  needsManualChoice: boolean;
  error: string | null;
};
export function setSourceEnabled(
  settings: PrefetchSettings,
  source: PlexSource,
  enabled: boolean,
): { settings: PrefetchSettings; error: string | null };
```

- [ ] Enforce the existing 128-source bound before adding defaults or a manually selected source; return a descriptive mutation error instead of silently truncating.

- [ ] Rerun the selection tests.

Expected: all pure mutation tests pass and retained child values are byte-for-byte equal.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/plex-source-selection.ts \
  frontend/app/routes/settings/smart-prefetch/plex-source-selection.test.ts
git commit -m "feat(ui): add Plex source switch behavior"
```

## Task 6: Build the Accordion Presentation Components

**Files:**

- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-toolbar.tsx`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-media-section.tsx`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-customization.tsx`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-source-components.test.tsx`

- [ ] Write component tests using fixed catalogue props rather than fetch mocks. Assert:

  - `Movies` and `TV shows` render as separate cards with summary text.
  - Library buttons expose `aria-expanded` and `aria-controls` and start collapsed.
  - Every section, library, source, and collection switch has a unique accessible name.
  - Collections are inside a nested collapsed disclosure.
  - An enabled source exposes `Customize`; opening it reveals limit, TV exclusions when applicable, and Preview.
  - Compact classes keep cards stacked and switch/button targets at least `min-h-11`.

- [ ] Run the test and confirm RED because the components do not exist:

```bash
cd frontend
npm test -- app/routes/settings/smart-prefetch/plex-source-components.test.tsx
```

- [ ] Implement `PlexSourceToolbar` with a connected-server selector, `Watching profile` selector, compact multi-user chooser, Refresh button, last-success text, and stale/error feedback. Preserve server-scoped user IDs (`serverId:userId`) and use an empty user array for `All Plex users`.

- [ ] Implement `PlexMediaSection` with controlled native `<details>` or disclosure buttons, a media master `Toggle`, per-library `Toggle`, hubs, nested collections, unavailable saved sources, summary counts, and a manual-choice message.

- [ ] Implement `PlexSourceCustomization` by extracting current limit, exclusion, preview, eligibility, and mapping-result behavior from `plex-sources.tsx`; do not change preview endpoints or payloads.

- [ ] Ensure all visual labels hide raw Plex `kind`, IDs, and `mixed` values. Put diagnostic identity only in existing error/preview details if needed.

- [ ] Rerun the component test.

Expected: all presentation tests pass with no network activity.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/plex-source-toolbar.tsx \
  frontend/app/routes/settings/smart-prefetch/plex-media-section.tsx \
  frontend/app/routes/settings/smart-prefetch/plex-source-customization.tsx \
  frontend/app/routes/settings/smart-prefetch/plex-source-components.test.tsx
git commit -m "feat(ui): add Plex source accordions"
```

## Task 7: Recompose Snapshot Loading Around the New UI

**Files:**

- Modify: `frontend/app/routes/settings/smart-prefetch/plex-sources.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`
- Modify: `frontend/app/routes/settings/plex/plex.component.test.tsx`

- [ ] Replace existing `Add source ...` test expectations with a complete fetch-backed flow containing one movie library, one TV library, duplicate hubs, recommendations, and collections.

- [ ] Add regression cases proving refresh retains draft selections, a failed/partial refresh retains the last successful catalogue and marks it stale, saved unavailable sources remain visible, no-server and no-compatible-library states are actionable, and a server saved/disconnected by `PlexSettings` updates this component without reload.

- [ ] Run the focused component tests and confirm RED against the old raw list:

```bash
cd frontend
npm test -- \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  app/routes/settings/plex/plex.component.test.tsx
```

- [ ] Refactor `PlexSources` into an orchestrator that keeps the current `loadPlexBootstrap`/`subscribePlexServers` behavior, fetches libraries/users/sources with abort protection, builds the catalogue, and delegates rendering/mutations to Tasks 4–6.

- [ ] Fetch sources for every compatible library plus global hubs with bounded `Promise.allSettled`, then merge successful snapshots. Retain the previous successful catalogue for failed areas, surface errors beside the affected area, and never replace settings state from refresh results.

- [ ] Preserve focus on the switch that was toggled. Open only the newly enabled library when no recommendation exists; otherwise keep accordions collapsed until the user expands them.

- [ ] Keep `PlexSources` exported with its existing `settings` and `onChange` props so the Plex saved-server synchronization test remains a focused cross-component contract.

- [ ] Rerun both focused component tests.

Expected: the new flow passes, and no test searches for `Add source` controls.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/plex-sources.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  frontend/app/routes/settings/plex/plex.component.test.tsx
git commit -m "feat(ui): simplify Plex source configuration"
```

## Task 8: Wire a Scoped Apply Through the Existing Save Path

**Files:**

- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`
- Modify: `frontend/app/routes/settings/streaming/streaming.tsx`
- Modify: `frontend/app/routes/settings/route.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`
- Modify: `frontend/app/routes/settings/streaming/streaming.test.ts`

- [ ] Extend the Smart Prefetch test harness with separate saved and draft configs plus a `persistConfigPatch` spy. Test that `Apply source changes`:

  - Is disabled when the Smart Prefetch draft equals the saved value.
  - Sends only `{ [PREFETCH_KEY]: currentDraft }`.
  - Leaves unrelated unsaved settings untouched.
  - Shows a concise success notice and clears only the source dirty indicator.
  - Shows failure feedback and keeps the draft dirty when persistence rejects.
  - Is unavailable when the setting is environment-managed.

- [ ] Run the focused tests and confirm RED because the props/action are absent:

```bash
cd frontend
npm test -- \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  app/routes/settings/streaming/streaming.test.ts
```

- [ ] Add these props through `route.tsx` → `StreamingSettings` → `SmartPrefetchSettings`:

```ts
savedConfig: Record<string, string>;
persistConfigPatch: (patch: Record<string, string>) => Promise<void>;
```

- [ ] In `route.tsx`, pass the already implemented `config` saved baseline and `persistConfigPatch`; do not add a new endpoint or duplicate the save implementation.

- [ ] In `SmartPrefetchSettings`, derive dirty state with `hasSmartPrefetchSettingsChanged(savedConfig, config)`, call `await persistConfigPatch({ [PREFETCH_KEY]: config[PREFETCH_KEY]! })`, and keep pending/success/error UI local to the source panel.

- [ ] Guard malformed or absent draft values and reuse `ManagedSetting` so environment-pinned settings remain visibly read-only.

- [ ] Rerun the focused tests.

Expected: scoped Apply tests pass and unrelated draft state survives.

- [ ] Commit:

```bash
git add frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx \
  frontend/app/routes/settings/streaming/streaming.tsx frontend/app/routes/settings/route.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  frontend/app/routes/settings/streaming/streaming.test.ts
git commit -m "feat(ui): apply Plex source changes locally"
```

## Task 9: Document the User Flow and Setup-Wizard Decision

**Files:**

- Modify: `docs/configuration/native-cache-prefetch.md`

- [ ] Replace the raw `Fetch libraries, users, hubs, and collections` instructions with the shipped flow: choose a watching profile, expand Movies or TV, switch on a library, review smart defaults, optionally expand Collections, customize only when needed, and use `Apply source changes`.

- [ ] Document exact defaults: movie libraries select Recently Added; TV libraries select On Deck and Continue Watching; collections remain off.

- [ ] Document that section and library switches retain source choices and customization, unavailable saved sources remain visible, and refresh never rewrites the draft.

- [ ] State the setup-wizard impact review explicitly: this remains an advanced optional Smart Prefetch setting, `DisabledLibraries` defaults empty, and neither the completion allowlist nor `SetupWizardService.CurrentWizardVersion` changes.

- [ ] Run the documentation formatter check on the edited file:

```bash
cd frontend
npx prettier --check ../docs/configuration/native-cache-prefetch.md
```

Expected: `Checking formatting... All matched files use Prettier code style!`

- [ ] Commit:

```bash
git add docs/configuration/native-cache-prefetch.md
git commit -m "chore(docs): explain Plex source accordions"
```

## Task 10: Focused Verification, Review, and Pull Request

**Files:**

- Review all files listed above.

- [ ] Run backend-focused verification from the repository root:

```bash
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj -c Release \
  --filter "FullyQualifiedName~PrefetchSettingsTests|FullyQualifiedName~PlexPolicyIntegrationTests"
```

Expected: all selected tests pass with zero failures.

- [ ] Run frontend-focused verification:

```bash
cd frontend
npm test -- \
  app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts \
  app/routes/settings/smart-prefetch/plex-source-catalogue.test.ts \
  app/routes/settings/smart-prefetch/plex-source-selection.test.ts \
  app/routes/settings/smart-prefetch/plex-source-components.test.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  app/routes/settings/plex/plex.component.test.tsx \
  app/routes/settings/streaming/streaming.test.ts
```

Expected: all selected Vitest files pass.

- [ ] Run generated-type and lint checks only if focused tests reveal a cross-file type/lint issue; otherwise rely on the repository's required PR CI lanes per `AGENTS.md`.

- [ ] Inspect `git diff --check`, `git status --short`, and `git diff main...HEAD`. Confirm `.claude-images/` and other user files are not staged.

- [ ] Review the implementation against every acceptance criterion in `docs/superpowers/specs/2026-09-21-plex-source-accordion-design.md`, especially library retention, global-hub identity, unavailable sources, environment-managed settings, and mobile/accessibility behavior.

- [ ] Request code review using `superpowers:requesting-code-review`; address findings with focused tests and separate Conventional Commits.

- [ ] Push the existing feature branch and open a PR only in the authorized repository:

```bash
git push -u origin feat/plex-source-accordions
gh pr create --repo johoja12/infinidysk --base main --head feat/plex-source-accordions \
  --title "feat(ui): simplify Plex source configuration" \
  --body-file /tmp/infinidysk-plex-source-pr.md
```

The PR body must summarize the Movies/TV accordion flow, persistent library gating, smart defaults, scoped Apply, focused tests, and the setup-wizard advanced-only decision. It must not claim deployment or runtime acceptance.

- [ ] Read the PR back with `gh pr view --repo johoja12/infinidysk` and verify its URL begins with `https://github.com/johoja12/infinidysk/`, its head SHA matches the pushed commit, and required checks have started.

- [ ] Do not merge or deploy without a new explicit request naming this PR. After PR handoff, restore the shared workspace to clean `main` as required by `AGENTS.md`.
