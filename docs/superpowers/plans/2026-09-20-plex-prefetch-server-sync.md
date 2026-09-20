# Plex Prefetch Server Synchronization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a successfully saved Plex server immediately selectable in Smart Prefetch without reloading the Streaming settings page.

**Architecture:** Add a typed in-browser publisher/subscriber boundary to the existing Plex API module. Plex settings publishes authoritative server arrays returned by successful save and refresh requests; Smart Prefetch subscribes while mounted, replaces its local list, and clears catalogue state if its selected server disappears.

**Tech Stack:** React 19, TypeScript 6, Vitest 5, Testing Library

---

## File Structure

- Modify `frontend/app/routes/settings/plex/plex-api.ts`: own the typed same-page Plex server notification boundary.
- Modify `frontend/app/routes/settings/plex/plex.tsx`: publish authoritative server arrays after successful save and refresh operations.
- Modify `frontend/app/routes/settings/smart-prefetch/plex-sources.tsx`: subscribe while mounted and invalidate a removed selection.
- Modify `frontend/app/routes/settings/plex/plex.component.test.tsx`: prove saving a manual server updates the mounted Smart Prefetch selector.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`: prove removing a selected server clears the selector and catalogue state.

### Task 1: Add failing cross-component regression coverage

**Files:**
- Test: `frontend/app/routes/settings/plex/plex.component.test.tsx`
- Test: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`

- [ ] **Step 1: Add an integration regression for a successful Plex save**

Import `PlexSources` and `parsePrefetchSettings` in `plex.component.test.tsx`, then add this test inside `describe("Plex settings", ...)`:

```tsx
it("makes a saved server available to Smart Prefetch without reloading", async () => {
  vi.stubGlobal("fetch", responses());
  render(
    <>
      <PlexSettings />
      <PlexSources settings={parsePrefetchSettings(undefined)} onChange={vi.fn()} />
    </>,
  );
  await screen.findByRole("button", { name: "Add manual server" });
  expect(screen.queryByRole("option", { name: "Home" })).toBeNull();
  await userEvent.click(screen.getByRole("button", { name: "Add manual server" }));
  await userEvent.type(screen.getByLabelText("Server 1 machine ID"), "machine");
  await userEvent.type(screen.getByLabelText("Server 1 name"), "Home");
  await userEvent.type(screen.getByLabelText("Server 1 URL"), "http://localhost:32400");
  await userEvent.type(screen.getByLabelText("Server 1 token"), "secret");
  await userEvent.click(screen.getByRole("button", { name: "Add path mapping for server 1" }));
  await userEvent.type(screen.getByLabelText("Server 1 mapping 1 Plex path"), "/Plex");
  await userEvent.selectOptions(screen.getByLabelText("Server 1 mapping 1 target type"), "local");
  await userEvent.type(screen.getByLabelText("Server 1 mapping 1 target path"), "/mnt/library");
  await userEvent.click(screen.getByRole("button", { name: "Save Plex servers" }));
  expect(await screen.findByRole("option", { name: "Home" })).toBeTruthy();
});
```

- [ ] **Step 2: Add a regression for invalidating a removed selection**

Import `act` from Testing Library and `publishPlexServers` from `../plex/plex-api` in `smart-prefetch.component.test.tsx`, then add:

```tsx
it("clears a selected source server when a refresh removes it", async () => {
  vi.stubGlobal("fetch", fakeApi());
  render(<Harness />);
  await screen.findByRole("option", { name: "Home" });
  const selector = screen.getByLabelText("Plex source server") as HTMLSelectElement;
  await userEvent.selectOptions(selector, "server");
  expect(selector.value).toBe("server");
  act(() => publishPlexServers([]));
  await waitFor(() => expect(selector.value).toBe(""));
  expect(screen.getByLabelText("Plex source library").hasAttribute("disabled")).toBe(true);
});
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run from `frontend/`:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex.component.test.tsx app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: FAIL because `publishPlexServers` does not exist and saving does not update the mounted Smart Prefetch selector. Do not change production code until this failure is observed.

### Task 2: Implement the typed server-list notification boundary

**Files:**
- Modify: `frontend/app/routes/settings/plex/plex-api.ts`
- Modify: `frontend/app/routes/settings/plex/plex.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/plex-sources.tsx`

- [ ] **Step 1: Add publisher and subscription functions**

Add this directly after the `PlexServer` type in `plex-api.ts`:

```ts
type PlexServersSubscriber = (servers: PlexServer[]) => void;
const plexServersSubscribers = new Set<PlexServersSubscriber>();

export function publishPlexServers(servers: PlexServer[]) {
  for (const subscriber of plexServersSubscribers) subscriber(servers);
}

export function subscribePlexServers(subscriber: PlexServersSubscriber) {
  plexServersSubscribers.add(subscriber);
  return () => {
    plexServersSubscribers.delete(subscriber);
  };
}
```

- [ ] **Step 2: Publish successful save and refresh responses**

Import `publishPlexServers` in `plex.tsx`. Update `refreshServers` and `save` so each publishes only after a successful backend response:

```ts
const refreshServers = async () => {
  const result = (await plexRequest<{ servers: PlexServer[] }>("servers")).servers;
  setServers(result);
  setSavedIds(result.map((server) => server.id));
  publishPlexServers(result);
};
```

```ts
const save = () =>
  act(async () => {
    const result = await plexRequest<{ servers: PlexServer[] }>("save", {
      servers: servers.map(serverSaveRequest),
    });
    setServers(result.servers);
    setSavedIds(result.servers.map((server) => server.id));
    publishPlexServers(result.servers);
    setMessage("Plex servers saved. General Apply is not required.");
  });
```

- [ ] **Step 3: Subscribe Smart Prefetch and clear removed selection state**

Import `subscribePlexServers` in `plex-sources.tsx`. Keep the bootstrap request in its existing mount effect and add a separate subscription effect:

```ts
useEffect(
  () =>
    subscribePlexServers((nextServers) => {
      setServers(nextServers);
    }),
  [],
);
```

Add an effect after the subscription that invalidates state tied to a server no longer present:

```ts
useEffect(() => {
  if (!serverId || servers.some((server) => server.id === serverId)) return;
  setServerId("");
  setLibraryId("");
  setLibraries(null);
  setUsers(null);
  setSources(null);
  setPreview(null);
  generation.current++;
}, [serverId, servers]);
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run from `frontend/`:

```bash
NODE_ENV=test npm test -- app/routes/settings/plex/plex.component.test.tsx app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: 2 test files pass, including both new regressions.

- [ ] **Step 5: Commit the behavior fix**

```bash
git add frontend/app/routes/settings/plex/plex-api.ts \
  frontend/app/routes/settings/plex/plex.tsx \
  frontend/app/routes/settings/smart-prefetch/plex-sources.tsx \
  frontend/app/routes/settings/plex/plex.component.test.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
git commit -m "fix(plex): refresh prefetch servers after saving"
```

### Task 3: Verify the completed frontend change

**Files:**
- Verify only; no expected source changes.

- [ ] **Step 1: Run frontend type checking**

Run from `frontend/`:

```bash
NODE_ENV=test npm run typecheck
```

Expected: exit 0 with no TypeScript errors.

- [ ] **Step 2: Run formatting validation for touched files**

Run from `frontend/`:

```bash
npx prettier --check \
  app/routes/settings/plex/plex-api.ts \
  app/routes/settings/plex/plex.tsx \
  app/routes/settings/smart-prefetch/plex-sources.tsx \
  app/routes/settings/plex/plex.component.test.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: all five files use Prettier formatting.

- [ ] **Step 3: Confirm branch scope and commit history**

Run from the worktree root:

```bash
git status --short
git diff --check origin/main...HEAD
git log --oneline origin/main..HEAD
```

Expected: clean worktree, no whitespace errors, and only the design/plan documentation plus the focused Plex fix commits.
