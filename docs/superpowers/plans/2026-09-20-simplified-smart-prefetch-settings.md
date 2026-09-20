# Simplified Smart Prefetch Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the all-at-once Smart Prefetch policy form with a small essential view backed by fixed defaults and a fully editable collapsed Advanced section.

**Architecture:** Keep the persisted `smart-prefetch.settings` JSON and backend contract unchanged. Add pure model helpers for policy customization, reset, and decimal-GB conversion; put policy presentation in a focused React component; leave Plex sources and queue operations untouched.

**Tech Stack:** React 19, TypeScript 6, React Router 8, daisyUI/Tailwind, Vitest, Testing Library.

---

## File structure

- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts`: pure default comparison, reset, and decimal-GB conversion helpers.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts`: model-helper red/green coverage.
- Create `frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx`: essential controls, grouped Advanced disclosure, customization state, and reset action.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`: delegate policy rendering while retaining parsing, persistence, Plex sources, and queue composition.
- Modify `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`: user-visible disclosure, preservation, reset, error, and managed-setting behavior.
- Modify `docs/configuration/native-cache-prefetch.md`: document the simplified view, fixed defaults, Advanced controls, and reset boundary.

### Task 1: Add pure policy-default helpers

**Files:**
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts`
- Test: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts`

- [ ] **Step 1: Write failing model tests**

Add these imports and tests:

```ts
import {
  bytesToDecimalGb,
  decimalGbToBytes,
  hasCustomizedPrefetchPolicy,
  resetPrefetchPolicyDefaults,
} from "./smart-prefetch-model";

it("converts the daily budget between bytes and decimal GB", () => {
  expect(bytesToDecimalGb(10_000_000_000)).toBe(10);
  expect(decimalGbToBytes(10.5)).toBe(10_500_000_000);
  expect(decimalGbToBytes(0)).toBe(0);
});

it("detects policy customization but ignores enablement, users and sources", () => {
  const defaults = parsePrefetchSettings(undefined);
  expect(hasCustomizedPrefetchPolicy(defaults)).toBe(false);
  expect(hasCustomizedPrefetchPolicy({ ...defaults, Enabled: true, Users: ["server:7"] })).toBe(
    false,
  );
  expect(hasCustomizedPrefetchPolicy({ ...defaults, MaxRetries: 4 })).toBe(true);
});

it("resets policy values while preserving enablement, users and sources", () => {
  const source = {
    ServerId: "server",
    LibraryId: "2",
    Kind: "hub",
    Key: "/hubs/recent",
    Title: "Recent",
    Type: "show",
    Enabled: true,
    Limit: 10,
    ExcludedShows: ["42"],
  };
  const reset = resetPrefetchPolicyDefaults({
    ...parsePrefetchSettings(undefined),
    Enabled: true,
    MaxRetries: 9,
    MovieEnabled: false,
    Users: ["server:7"],
    Sources: [source],
  });
  expect(reset.Enabled).toBe(true);
  expect(reset.MaxRetries).toBe(3);
  expect(reset.MovieEnabled).toBe(true);
  expect(reset.Users).toEqual(["server:7"]);
  expect(reset.Sources).toEqual([source]);
});
```

- [ ] **Step 2: Run the model test and verify RED**

Run:

```bash
cd frontend
NODE_ENV=test npm test -- --run app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts
```

Expected: FAIL because the four helper exports do not exist.

- [ ] **Step 3: Implement the helpers**

Add after the default objects in `smart-prefetch-model.ts`:

```ts
export const DECIMAL_GB_BYTES = 1_000_000_000;

export function bytesToDecimalGb(bytes: number): number {
  return bytes / DECIMAL_GB_BYTES;
}

export function decimalGbToBytes(gigabytes: number): number {
  return Math.round(gigabytes * DECIMAL_GB_BYTES);
}
```

Add after the `PrefetchSettings` type:

```ts
const customizableBooleanKeys = Object.keys(booleanDefaults).filter(
  (key): key is keyof typeof booleanDefaults => key !== "Enabled",
);
const customizableNumericKeys = Object.keys(numericDefaults) as (keyof typeof numericDefaults)[];

export function hasCustomizedPrefetchPolicy(settings: PrefetchSettings): boolean {
  return (
    customizableBooleanKeys.some((key) => settings[key] !== booleanDefaults[key]) ||
    customizableNumericKeys.some((key) => settings[key] !== numericDefaults[key])
  );
}

export function resetPrefetchPolicyDefaults(settings: PrefetchSettings): PrefetchSettings {
  return {
    ...settings,
    ...numericDefaults,
    ...booleanDefaults,
    Enabled: settings.Enabled,
  };
}
```

- [ ] **Step 4: Run the model test and verify GREEN**

Run the Step 2 command. Expected: all model tests PASS.

- [ ] **Step 5: Commit the model helpers**

```bash
git add frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.ts \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts
git commit -m "feat(ui): add Smart Prefetch policy default helpers"
```

### Task 2: Introduce the essential view and grouped Advanced disclosure

**Files:**
- Create: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`
- Test: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`

- [ ] **Step 1: Write the failing disclosure test**

Add this component test:

```tsx
it("shows essential policy choices and keeps expert controls in a collapsed disclosure", async () => {
  vi.stubGlobal("fetch", fakeApi());
  render(<Harness />);
  expect(screen.getByLabelText("Enable Smart Prefetch")).toBeTruthy();
  expect(screen.getByLabelText("Warm movies")).toBeTruthy();
  expect(screen.getByLabelText("Warm TV episodes")).toBeTruthy();
  expect(screen.getByLabelText("Daily download budget (GB/day)")).toBeTruthy();
  expect(screen.getByText("Smart defaults")).toBeTruthy();
  const details = screen.getByText("Advanced settings").closest("details");
  expect(details?.open).toBe(false);
  expect(details?.contains(screen.getByLabelText("Episodes to queue ahead"))).toBe(true);
  await userEvent.click(screen.getByText("Advanced settings"));
  expect(details?.open).toBe(true);
});
```

In the existing `edits complete typed controls...` test, expand `Advanced settings` before editing `Episodes to queue ahead`:

```ts
await userEvent.click(screen.getByText("Advanced settings"));
```

- [ ] **Step 2: Run the component test and verify RED**

Run:

```bash
cd frontend
NODE_ENV=test npm test -- --run app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: FAIL because `Advanced settings` and the GB/day budget control do not exist.

- [ ] **Step 3: Create the policy-controls component**

Create `smart-prefetch-policy-controls.tsx` with these typed groups and controls:

```tsx
import { Input, InputGroup, Toggle } from "~/components/ui";
import {
  bytesToDecimalGb,
  decimalGbToBytes,
  numericFields,
  type NumericKey,
  type PrefetchSettings,
} from "./smart-prefetch-model";

type BooleanKey = keyof Pick<
  PrefetchSettings,
  | "HistoryEnabled"
  | "RealtimeEnabled"
  | "ReadActivityEnabled"
  | "PredictionsEnabled"
  | "MinimumWarmEnabled"
  | "FullFileWarming"
  | "WarmLocalFiles"
  | "PauseDuringPlayback"
>;

const booleanGroups: { title: string; fields: { key: BooleanKey; label: string }[] }[] = [
  {
    title: "Signals",
    fields: [
      { key: "HistoryEnabled", label: "Use Plex watch history" },
      { key: "RealtimeEnabled", label: "Use verified Plex playback" },
      { key: "ReadActivityEnabled", label: "Use raw read-activity signals" },
      { key: "WarmLocalFiles", label: "Allow mapped local library files" },
    ],
  },
  {
    title: "Predictions",
    fields: [{ key: "PredictionsEnabled", label: "Predict upcoming episodes" }],
  },
  {
    title: "Warming",
    fields: [
      { key: "MinimumWarmEnabled", label: "Warm minimum head and tail ranges" },
      { key: "FullFileWarming", label: "Warm full files" },
      { key: "PauseDuringPlayback", label: "Pause background warming during playback" },
    ],
  },
];

const numericGroups: { title: string; keys: NumericKey[] }[] = [
  {
    title: "Predictions",
    keys: [
      "ConfidenceThreshold",
      "CooldownMinutes",
      "LookbackDays",
      "MoviePartialWatchLookbackDays",
      "MinEpisodesForPrediction",
      "MaxQueueAhead",
      "TvEpisodesPerShow",
    ],
  },
  {
    title: "Scheduling",
    keys: [
      "SyncIntervalMinutes",
      "RealtimeCheckIntervalSeconds",
      "MovieSyncIntervalMinutes",
      "TvSyncIntervalMinutes",
      "VerifiedSessionExpirySeconds",
      "IntentTtlHours",
    ],
  },
  {
    title: "Queue and resources",
    keys: [
      "QueueCapacity",
      "MaxRetries",
      "MaxConcurrentJobs",
      "ConnectionsPerJob",
      "MaxBytesPerItem",
    ],
  },
  { title: "Warming", keys: ["MinimumHeadMb", "MinimumTailMb"] },
];

export function SmartPrefetchPolicyControls({
  settings,
  onChange,
}: {
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
}) {
  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2">
        <Toggle
          label="Enable Smart Prefetch"
          checked={settings.Enabled}
          onChange={(event) => onChange({ ...settings, Enabled: event.target.checked })}
        />
        <Toggle
          label="Warm movies"
          checked={settings.MovieEnabled}
          onChange={(event) => onChange({ ...settings, MovieEnabled: event.target.checked })}
        />
        <Toggle
          label="Warm TV episodes"
          checked={settings.TvEnabled}
          onChange={(event) => onChange({ ...settings, TvEnabled: event.target.checked })}
        />
        <label>
          Daily download budget
          <InputGroup
            aria-label="Daily download budget (GB/day)"
            type="number"
            min={0}
            max={100_000}
            step={0.1}
            suffix="GB/day"
            value={bytesToDecimalGb(settings.DailyByteBudget)}
            onChange={(event) =>
              onChange({
                ...settings,
                DailyByteBudget: decimalGbToBytes(Number(event.target.value)),
              })
            }
          />
          <small className="block text-xs text-base-content/50">
            0 means unlimited. Provider attempts count; cache hits do not.
          </small>
        </label>
      </div>
      <div className="rounded-box border border-base-content/10 bg-base-200/40 p-3">
        <strong>Smart defaults</strong>
        <p className="text-xs text-base-content/60">
          Verified playback, next episodes, whole files, pause during playback, and conservative
          concurrency.
        </p>
      </div>
      <details className="collapse collapse-arrow border border-base-content/10 bg-base-200/40">
        <summary className="collapse-title text-sm font-semibold">Advanced settings</summary>
        <div className="collapse-content space-y-5">
          {booleanGroups.map((group) => (
            <section key={`boolean-${group.title}`} className="space-y-2">
              <h4 className="font-semibold">{group.title}</h4>
              <div className="grid gap-3 md:grid-cols-2">
                {group.fields.map((field) => (
                  <Toggle
                    key={field.key}
                    label={field.label}
                    checked={settings[field.key]}
                    onChange={(event) =>
                      onChange({ ...settings, [field.key]: event.target.checked })
                    }
                  />
                ))}
              </div>
            </section>
          ))}
          {numericGroups.map((group) => (
            <section key={`numeric-${group.title}`} className="space-y-2">
              <h4 className="font-semibold">{group.title}</h4>
              <div className="grid gap-3 md:grid-cols-2">
                {group.keys.map((key) => {
                  const field = numericFields.find((candidate) => candidate.key === key)!;
                  return (
                    <label key={key}>
                      {field.label}
                      <Input
                        aria-label={field.label}
                        type="number"
                        min={field.min}
                        max={field.max}
                        step={field.step ?? 1}
                        value={settings[key]}
                        onChange={(event) =>
                          onChange({ ...settings, [key]: Number(event.target.value) })
                        }
                      />
                      <small className="block text-xs text-base-content/50">
                        {field.min.toLocaleString()}–{field.max.toLocaleString()}
                      </small>
                    </label>
                  );
                })}
              </div>
            </section>
          ))}
        </div>
      </details>
    </div>
  );
}
```

- [ ] **Step 4: Delegate policy rendering from the parent**

In `smart-prefetch.tsx`, remove the local `toggles` metadata and the `numericFields` import. Import the new component:

```ts
import { SmartPrefetchPolicyControls } from "./smart-prefetch-policy-controls";
```

Replace the toggle grid, raw-read explanatory paragraph, numeric grid, and trailing budget paragraph with:

```tsx
<SmartPrefetchPolicyControls settings={settings} onChange={update} />
<p className="text-xs">
  Raw read activity is not verified Plex playback. Local-library eligibility still requires a
  symlink or STRM path that resolves to an imported DAV file; regular local files are skipped.
  Connection and concurrency caps do not create extra provider capacity, and all warming remains
  below foreground playback admission.
</p>
```

Change the card description to:

```tsx
description="Off by default. Start with fixed, safe policy defaults; expand Advanced only when you need to tune scheduling, predictions, or provider work. Save Native cache settings and restart before enabling."
```

- [ ] **Step 5: Run component and model tests and verify GREEN**

```bash
cd frontend
NODE_ENV=test npm test -- --run \
  app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
```

Expected: both files PASS.

- [ ] **Step 6: Commit the essential and Advanced layout**

```bash
git add frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
git commit -m "feat(ui): simplify Smart Prefetch policy controls"
```

### Task 3: Add customization, reset, and error disclosure behavior

**Files:**
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx`
- Modify: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx`
- Test: `frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx`

- [ ] **Step 1: Extend the test harness and write failing behavior tests**

Import `PREFETCH_KEY` and `type PrefetchSettings` beside `parsePrefetchSettings`, then replace the harness signature and initial state with:

```tsx
function Harness({
  managed = false,
  initial,
}: {
  managed?: boolean;
  initial?: PrefetchSettings;
}) {
  const [config, setConfig] = useState<Record<string, string>>(
    initial ? { [PREFETCH_KEY]: JSON.stringify(initial) } : {},
  );
  return (
    <ManagedEnvProvider
      value={
        managed ? { "smart-prefetch.settings": "NZBDAV_CONFIG__SMART_PREFETCH__SETTINGS" } : {}
      }
    >
      <SmartPrefetchSettings config={config} setNewConfig={setConfig} />
      <output data-testid="config">{JSON.stringify(config)}</output>
    </ManagedEnvProvider>
  );
}
```

Add these tests:

```tsx
it("marks customized policy and resets defaults without losing identity selections", async () => {
  vi.stubGlobal("fetch", fakeApi());
  const initial = {
    ...parsePrefetchSettings(undefined),
    Enabled: true,
    MaxRetries: 9,
    Users: ["server:7"],
    Sources: [
      {
        ServerId: "server",
        LibraryId: "2",
        Kind: "hub",
        Key: "/hubs/recent",
        Title: "Recent TV",
        Type: "show",
        Enabled: true,
        Limit: 10,
        ExcludedShows: ["42"],
      },
    ],
  };
  render(<Harness initial={initial} />);
  expect(screen.getByText("Customized")).toBeTruthy();
  expect(screen.getByText("Customized policy")).toBeTruthy();
  await userEvent.click(screen.getByText("Advanced settings"));
  await userEvent.click(screen.getByRole("button", { name: "Reset to smart defaults" }));
  const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
  const saved = parsePrefetchSettings(config["smart-prefetch.settings"]);
  expect(saved.Enabled).toBe(true);
  expect(saved.MaxRetries).toBe(3);
  expect(saved.Users).toEqual(["server:7"]);
  expect(saved.Sources).toEqual(initial.Sources);
});

it("preserves hidden custom values when editing an essential control", async () => {
  vi.stubGlobal("fetch", fakeApi());
  render(
    <Harness
      initial={{ ...parsePrefetchSettings(undefined), MaxBytesPerItem: 123_000_000_000 }}
    />,
  );
  await userEvent.click(screen.getByLabelText("Warm movies"));
  const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
  const saved = parsePrefetchSettings(config["smart-prefetch.settings"]);
  expect(saved.MovieEnabled).toBe(false);
  expect(saved.MaxBytesPerItem).toBe(123_000_000_000);
});

it("opens Advanced settings when an advanced value is invalid", async () => {
  vi.stubGlobal("fetch", fakeApi());
  render(<Harness initial={{ ...parsePrefetchSettings(undefined), MaxRetries: 11 }} />);
  expect(
    screen.getByText(
      "Retries after failure (0 means no retries) must be a whole number from 0 to 10.",
    ),
  ).toBeTruthy();
  await waitFor(() =>
    expect(screen.getByText("Advanced settings").closest("details")?.open).toBe(true),
  );
});
```

- [ ] **Step 2: Run the component test and verify RED**

Run the Task 2 Step 2 command. Expected: FAIL because no Customized badge, reset action, or automatic error expansion exists.

- [ ] **Step 3: Implement customization and reset behavior**

In `smart-prefetch-policy-controls.tsx`, import `useEffect`, `useRef`, `Badge`, `Button`, `hasCustomizedPrefetchPolicy`, and `resetPrefetchPolicyDefaults`. Add `error: string | null` to the props and calculate:

```tsx
const details = useRef<HTMLDetailsElement>(null);
const customized = hasCustomizedPrefetchPolicy(settings);
useEffect(() => {
  if (error && details.current) details.current.open = true;
}, [error]);
```

Change the summary card heading to:

```tsx
<strong>{customized ? "Customized policy" : "Smart defaults"}</strong>
```

Replace the disclosure opening and summary with this exact diff:

```diff
-<details className="collapse collapse-arrow border border-base-content/10 bg-base-200/40">
-  <summary className="collapse-title text-sm font-semibold">Advanced settings</summary>
+<details
+  ref={details}
+  className="collapse collapse-arrow border border-base-content/10 bg-base-200/40"
+>
+  <summary className="collapse-title flex items-center gap-2 text-sm font-semibold">
+    <span>Advanced settings</span>
+    {customized && <Badge className="badge-warning badge-soft badge-sm">Customized</Badge>}
+  </summary>
```

Inside the existing `collapse-content` div, insert this as its first child; keep both existing group render blocks immediately after it:

```tsx
<div className="flex justify-end">
  <Button
    type="button"
    variant="ghost"
    onClick={() => onChange(resetPrefetchPolicyDefaults(settings))}
  >
    Reset to smart defaults
  </Button>
</div>
```

Pass the parse/validation error from `smart-prefetch.tsx`:

```tsx
<SmartPrefetchPolicyControls settings={settings} error={error} onChange={update} />
```

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 2 Step 5 command. Expected: both files PASS.

- [ ] **Step 5: Commit customization behavior**

```bash
git add frontend/app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.tsx \
  frontend/app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
git commit -m "feat(ui): expose Smart Prefetch customization safely"
```

### Task 4: Document and verify the complete UI change

**Files:**
- Modify: `docs/configuration/native-cache-prefetch.md`
- Verify: all files changed in Tasks 1–3

- [ ] **Step 1: Update the operator guide**

At the start of `Select policies and inspect work`, add:

```md
The normal Smart Prefetch view contains only the master switch, movie and TV
eligibility, and the daily provider-payload budget in decimal GB. Fixed smart
defaults handle verified playback, next-episode prediction, whole-file warming,
playback pausing, and conservative concurrency. Expand **Advanced settings** to
change signals, prediction thresholds, schedules, queue resources, or warming
ranges. **Customized** means at least one policy value differs from those defaults.
**Reset to smart defaults** restores policy behavior without disabling Smart
Prefetch or removing selected Plex users, hubs, or collections.
```

- [ ] **Step 2: Run focused frontend verification**

```bash
cd frontend
NODE_ENV=test npm test -- --run \
  app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
npx prettier --check \
  app/routes/settings/smart-prefetch/smart-prefetch-model.ts \
  app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts \
  app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx \
  ../docs/configuration/native-cache-prefetch.md
npx eslint --max-warnings 0 \
  app/routes/settings/smart-prefetch/smart-prefetch-model.ts \
  app/routes/settings/smart-prefetch/smart-prefetch-model.test.ts \
  app/routes/settings/smart-prefetch/smart-prefetch-policy-controls.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.tsx \
  app/routes/settings/smart-prefetch/smart-prefetch.component.test.tsx
npm run typecheck
```

Expected: focused tests PASS, Prettier reports all listed files formatted, ESLint exits 0 with no warnings, and TypeScript exits 0.

- [ ] **Step 3: Inspect the exact diff and setup-wizard boundary**

```bash
cd ..
git diff --check
git diff --stat origin/main...HEAD
git diff origin/main...HEAD -- \
  frontend/app/routes/settings/smart-prefetch \
  docs/configuration/native-cache-prefetch.md
git diff --quiet origin/main...HEAD -- \
  backend/Config/ConfigKeys.cs \
  backend/Services/SetupWizardService.cs \
  frontend/app/routes/setup
```

Expected: no whitespace errors; only the intended Smart Prefetch UI/model/tests and documentation differ; the final command exits 0, proving no setup-wizard or configuration-key change.

- [ ] **Step 4: Commit documentation**

```bash
git add docs/configuration/native-cache-prefetch.md
git commit -m "chore(docs): explain simplified Smart Prefetch policies"
```

- [ ] **Step 5: Prepare the branch handoff**

Verify `git status --short` is empty. Then use `superpowers:requesting-code-review` and `superpowers:finishing-a-development-branch`. Per `AGENTS.md`, the expected handoff is a pushed `feat/simplify-smart-prefetch-settings` branch and a PR to `main` in `johoja12/infinidysk`, without merging or deploying unless the user separately authorizes those actions.
