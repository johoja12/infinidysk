# Detached replacement recovery and controls — 2026-11-04

Rust base: `652a7558287d8214469a5c9029dc4569a036ae5c`.
Specification remains fork 1.x `9c104b5961abe67c14e6565932e48ea340f1dbd3`.

A separate observer processes three oldest-due journals every 15 seconds, with a
five-minute per-record interval and a ten-second network deadline. Every observation,
including unavailable/malformed cases, updates its durable scheduling checkpoint.
It performs GETs only, preserves uncertain stages, and survives local source removal.
New records bind a credential-free Arr identity digest; older detached records without
that proof require review. A changed source/Arr cannot become mutation authority.

Authenticated controls observe, abandon or resume using an expected journal revision.
Resume retains the last durable mutation stage and renews the review interval; it
never resets an uncertain action to Prepared. Abandon stops future coordination for
that request, retains its outcome/history and does not undo an accepted/in-flight Arr
action. Completed replacements cannot be resumed. All checkpoints use revision CAS.

Before any new blocklist, delete or search, the coordinator verifies a normalized
exact recorded file path under a current non-root Arr library root. Traversal,
partial-prefix matches, malformed roots, unavailable root lists and stale source
identity prevent dispatch. Rust asks Arr to delete an exact managed file ID; it does
not implement the C# direct local-symlink unlink route. Read-only scan-root policy is
part of the remaining library/migration work.

The Health page exposes source-independent recovery history, stable cursor paging,
observation and controls. Unreadable records remain visible without returning their
raw payload. No setting, setup-wizard field or database migration is introduced.
Existing additive migration backup requirements remain in effect.

Validation:

- Frozen workspace: **453 passed, zero failed, six ignored**, with actual PostgreSQL
  and SQLite. Formatting, all-target Clippy, API schema and TypeScript/Vite build pass.
- Existing Arr/health fixtures now provide explicit root authority. Write expectations
  remain unchanged. The root guard rejects unrelated, traversal, partial-prefix and
  filesystem-root responses before dispatch.
- The restart regression proves revision rejection, abandon/resume, source removal,
  unavailable configuration, later replacement observation and no repeated mutations.
- Actual configured app/API + Chromium with a loopback Arr: DELETE takes effect then
  returns 500; resume observes it rather than deleting twice. Search is accepted then
  returns 500; Check now finds its command without reposting. Abandon/resume and stale
  revision rejection pass. After deleting the generated local source, the request
  remains visible and observes the replacement. Exactly **one delete and one search**.
- An additional 50 synthetic terminal history records test 51-row cursor paging only;
  those records are distinct from the actual mutation/recovery evidence above.
- The 390px page fits without overflow and reports no browser errors.

Local receipt: `/tmp/infinidysk-rust-browser/regrab-controls-validation.json`.
Canary binary SHA-256:
`9649f56a15da36d1cab8b71b00e897e3ecc3f831e80988c3a939abc772734bf7`.
No production Arr, provider, media, cache or deployment is used. Original C# writer
and privileged FUSE evidence from the upstream-sync checkpoint remains separate;
this increment does not change their source paths. Full sweep, PAR2 execution,
prefetch governor and broader migration remain pending.
