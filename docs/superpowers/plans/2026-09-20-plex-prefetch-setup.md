# Plex and Smart Prefetch Setup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make discovered Plex servers save through one explicit action and keep secondary Smart Prefetch configuration collapsed by default.

**Architecture:** Preserve the existing dedicated `/api/plex/*` persistence boundary and in-browser server publisher. Refactor only the React presentation/state flow: a selected discovery candidate is converted to a server and sent in one authoritative save request, while Smart Prefetch retains its existing model and moves secondary components behind disclosures.

**Tech Stack:** React Router 8, React, TypeScript, DaisyUI/Tailwind, Vitest, Testing Library.

---

### Task 1: Save a selected discovered Plex server directly

**Files:**
- Modify: `frontend/app/routes/settings/plex/plex.component.test.tsx`
- Modify: `frontend/app/routes/settings/plex/plex.tsx`

- [ ] **Step 1: Write the failing discovery-save component test**

Add a test with one saved account and two discovered candidates. Assert that no `/api/plex/save` request occurs before a candidate is selected, select `Home`, click **Save selected server**, and assert the request includes `"handle":"home-handle"` and retains an unrelated saved server. Assert the specific success status is rendered.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex.component.test.tsx
```

Expected: FAIL because candidate selection cards and **Save selected server** do not exist.

- [ ] **Step 3: Implement the minimal selected-candidate save flow**

In `PlexSettings`, add `selectedCandidateHandle`, reset it when discovery results change, and derive the selected candidate. Replace each **Choose** button with a radio-card selection. Add a `saveServers(nextServers, successMessage)` helper that sends `nextServers.map(serverSaveRequest)`, adopts the response, updates `savedIds`, publishes the response, and sets the supplied message. The primary action must build the selected server, merge it by machine ID while preserving mappings, and invoke that helper.

- [ ] **Step 4: Move legacy editing into Advanced server configuration**

Wrap saved/manual server editors and their save action in a collapsed `<details>` disclosure. Rename the action **Save advanced server changes** and disable it when the list is empty or invalid. Keep removal, testing, manual-token configuration, and mappings unchanged.

- [ ] **Step 5: Run the focused test and verify GREEN**

Run:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex.component.test.tsx
```

Expected: all Plex component tests pass.

- [ ] **Step 6: Commit the Plex behavior**

```bash
git add frontend/app/routes/settings/plex/plex.tsx frontend/app/routes/settings/plex/plex.component.test.tsx
git commit -m "fix(plex): save detected servers in one step"
```

### Task 2: Match the approved Smart Prefetch default view

**Files:**
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`
- Create: `frontend/app/routes/settings/smart-prefetch/plex-server-status.tsx`

- [ ] **Step 1: Write failing default-visibility tests**

Assert the default view exposes labels **Enable Smart Prefetch**, **Movies**, **TV episodes**, and **Daily download budget (GB/day)**. Assert advanced policy controls, the Plex source server selector, and queue action controls are not visible. Open **Plex libraries and sources** and **Prefetch activity** and assert their controls become visible. Add a focused status test for one enabled server.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
NODE_ENV=test npm test -- app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: FAIL because the old `Warm` labels remain and the source/queue sections are expanded.

- [ ] **Step 3: Implement the approved hierarchy**

Rename the two eligibility labels to **Movies** and **TV episodes**, and change the summary heading to **Smart defaults are on**. Add `PlexServerStatus`, using `loadPlexBootstrap` plus `subscribePlexServers`, to render one enabled server name, an enabled-server count, or a not-connected state without exposing credentials.

- [ ] **Step 4: Collapse secondary setup and operations**

Keep the policy card visible. Wrap `PlexSources` in a collapsed **Plex libraries and sources** disclosure and `PrefetchQueue` in a collapsed **Prefetch activity** disclosure. Do not change their data flow or persistence ownership.

- [ ] **Step 5: Run focused tests and verify GREEN**

Run:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex.component.test.tsx app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: both files pass with no console errors.

- [ ] **Step 6: Commit the Smart Prefetch UX**

```bash
git add frontend/app/routes/settings/smart-prefetch
git commit -m "fix(ui): simplify Plex prefetch setup"
```

### Task 3: Document and verify the complete issue

**Files:**
- Modify: `docs/configuration/native-cache-prefetch.md`

- [ ] **Step 1: Update the operator workflow**

Document the direct detected-server save action, persistence confirmation, collapsed advanced server editor, compact Plex status, and collapsed source/activity sections. Retain the existing `since` pill and setup-wizard decision because no new configuration key is introduced.

- [ ] **Step 2: Run verification**

Run:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex-api.test.ts app/routes/settings/plex/plex.component.test.tsx app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
NODE_ENV=test npm run typecheck
NODE_ENV=test npm run lint
NODE_ENV=test npm run format:check
```

Expected: all commands exit 0.

- [ ] **Step 3: Commit documentation**

```bash
git add docs/configuration/native-cache-prefetch.md
git commit -m "chore(docs): explain direct Plex prefetch setup"
```

- [ ] **Step 4: Review and release**

Inspect the diff against `origin/main`, request code review, fix Critical or Important findings, push `fix/plex-prefetch-setup`, create a `fix(ui)` PR referencing #12, verify its head SHA and checks, merge that exact PR as explicitly authorized, and confirm `origin/main` contains the merge.

- [ ] **Step 5: Deploy and verify production**

Build and deploy an immutable image from the merged main SHA using the documented nuc-1 process. Recreate only the `infinidysk` application service. Verify container health/restarts, runtime SHA, public and LAN health, the four-control default view, direct selected-server persistence across reload, immediate Smart Prefetch visibility, and no browser console errors. Close #12 only after live acceptance and set project Status to Done if project permissions permit.
