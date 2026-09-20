# Simplified Smart Prefetch Settings Design

## Goal

Make Smart Prefetch approachable without removing expert control. The normal view will expose only the decisions most operators need, use fixed and predictable smart defaults for every other policy, and keep the complete policy surface available in a collapsed Advanced section.

## Scope

This is a frontend presentation and settings-model refactor. It does not change the backend `PrefetchSettings` contract, stored JSON schema, database, runtime scheduling, queue semantics, Plex catalogue APIs, native-cache requirements, or setup wizard. Existing configurations must round-trip without losing or rewriting hidden values.

## Default view

The Smart Prefetch policies card will show:

- `Enable Smart Prefetch`.
- `Warm movies`.
- `Warm TV episodes`.
- `Daily download budget`, displayed and edited in decimal GB per day while remaining stored as bytes in the existing JSON field.
- A compact Smart defaults summary explaining the fixed policy: verified Plex playback, next-episode predictions, whole-file warming, pausing during foreground playback, and conservative concurrency. When behavior values differ from defaults, the summary changes to `Customized policy` so it never claims customized behavior is still the default.

The Plex libraries, users, and sources card remains visible and unchanged below the policy card. The prefetch queue and immediate operations also remain unchanged.

## Fixed smart defaults

The existing model defaults remain the single source of truth. Missing fields are normalized to those defaults when settings are parsed. The simplification does not introduce adaptive or workload-dependent tuning.

The initial defaults remain predictable and user-editable: Smart Prefetch is off, movies and TV are eligible, verified playback and next-episode prediction are enabled, whole files are warmed, foreground playback pauses background warming, concurrency stays conservative, and the daily provider-payload budget is 10 GB.

## Advanced settings

All controls other than the four default-view controls move into a collapsed `Advanced settings` disclosure. When expanded, controls are grouped as:

- Signals: history, verified playback, raw read activity, and local mapped-file eligibility.
- Predictions: prediction enablement, confidence, cooldown, lookbacks, watched-episode threshold, and queue-ahead limits.
- Scheduling: history, playback, movie-source, and TV-source intervals plus verified-session expiry and intent lifetime.
- Queue and resources: capacity, retries, concurrent jobs, connections per job, and maximum bytes per item.
- Warming: whole-file versus minimum-range controls, head and tail sizes, and pause-during-playback behavior.

The disclosure remains collapsed by default even when values are customized. A `Customized` badge appears whenever any behavior value differs from the fixed defaults. Existing Plex `Users` and `Sources` do not affect this badge.

`Reset to smart defaults` restores boolean and numeric policy values to the model defaults while preserving the current `Enabled` value, Plex users, and selected sources. It is an explicit action and must not run when the disclosure is opened or when unrelated fields are edited.

## Compatibility and validation

Existing saved policy fields stay in the settings object even while their controls are hidden. Every update spreads from the complete parsed settings object so unrelated values survive. Environment-managed Smart Prefetch remains entirely read-only through the existing `ManagedSetting` boundary.

The daily budget control uses decimal GB (`1 GB = 1,000,000,000 bytes`), accepts 0.1 GB steps, rounds an edited value to the nearest whole byte, and converts existing bytes to GB for display without changing an untouched stored value. Zero continues to mean unlimited, and the existing byte bounds remain authoritative. Invalid advanced values automatically open the disclosure so the operator can see and correct the offending control. Malformed saved JSON retains the existing blocking error rather than silently replacing it with defaults.

No new `ConfigKeys` setting is introduced, so the setup wizard, environment mapping, documentation version, and wizard completion allowlist require no changes.

## Accessibility and responsive behavior

The Advanced disclosure uses native or equivalent accessible disclosure semantics, exposes its expanded state, and is keyboard operable. The Customized badge is textual rather than color-only. Reset has a clear accessible name. Essential controls remain usable in one column on narrow screens and use the existing two-column layout when space permits.

## Test strategy

Focused component tests will prove:

- only the four essential controls are visible initially;
- Advanced reveals every existing expert control;
- hidden custom values survive essential-field edits;
- the Customized badge reflects deviations from fixed defaults while ignoring users and sources;
- Reset restores behavior defaults but preserves `Enabled`, users, and sources;
- invalid advanced values expand the disclosure and retain the validation error;
- environment-owned policies remain disabled.

Focused model tests will cover the customized-state helper, reset helper, and decimal-GB/byte conversion including bounds. Verification will include the focused Vitest files plus frontend formatting, lint, and type-check commands.

## Acceptance criteria

An operator can enable Smart Prefetch, choose movies and TV, and set a daily budget without seeing implementation-level tuning. An expert can still edit every existing setting. Existing policy JSON remains compatible and lossless. No backend or database change is introduced.
