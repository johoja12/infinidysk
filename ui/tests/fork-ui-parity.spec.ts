import { test, expect } from "@playwright/test";
const fixture = (screen: string) =>
  `/tests/fork-ui-parity.html?screen=${screen}`;
test("fork feature navigation is visible", async ({ page }) => {
  await page.goto(fixture("nav"));
  for (const label of ["Native Cache", "Library", "Smart Prefetch"])
    await expect(
      page.getByRole("link", { name: label, exact: true }),
    ).toBeVisible();
  await expect(
    page.getByRole("link", { name: "Media Library", exact: true }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("link", { name: "Plex Library", exact: true }),
  ).toHaveCount(0);
});
test("original cache is visible, protected and inspectable", async ({
  page,
}, info) => {
  await page.goto(fixture("cache"));
  await expect(
    page.getByRole("heading", { name: "Original cache storage" }),
  ).toBeVisible();
  for (let i = 1; i <= 4; i++)
    await expect(
      page.getByRole("heading", { name: `fixture-folder-${i}` }),
    ).toBeVisible();
  await expect(
    page.getByText(/12 hits · 3 misses since restart/),
  ).toBeVisible();
  await page.getByRole("tab", { name: "Files", exact: true }).click();
  const file = page.getByRole("row").filter({ hasText: "Fixture cached file" });
  const protectedFile = page
    .getByRole("row")
    .filter({ hasText: "Fixture protected file" });
  await expect(
    protectedFile.getByRole("button", { name: "Pin", exact: true }),
  ).toBeDisabled();
  await expect(
    file.getByRole("button", { name: "Evict", exact: true }),
  ).toBeDisabled();
  await file.getByRole("button", { name: "Pin", exact: true }).click();
  await expect(
    file.getByRole("button", { name: "Unpin", exact: true }),
  ).toBeVisible();
  await file.getByRole("button", { name: "Ranges" }).click();
  await page.getByRole("button", { name: "Verify cached sample" }).click();
  await expect(
    page.getByText("4.0 MB verified against the catalogue hash"),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      window.__mock.calls
        .filter((c) => c.method === "POST")
        .every(
          (c) => (c.data.entry ?? c.data).generation === "fixture-generation",
        ),
    ),
  ).toBe(true);
  await page.getByRole("button", { name: "Close ranges", exact: true }).click();
  await page.getByRole("tab", { name: "Diagnostics", exact: true }).click();
  await page
    .getByRole("button", { name: "Check cache storage", exact: true })
    .click();
  await expect(page.getByText(/Original folder identity verified/)).toHaveCount(
    4,
  );
  await page.screenshot({
    path: info.outputPath("generated-native-cache.png"),
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
test("named source settings preview, retain selections and preserve concurrent changes", async ({
  page,
}, info) => {
  await page.goto(fixture("settings"));
  await expect(
    page.getByRole("combobox", { name: "Source profile", exact: true }),
  ).toBeVisible();
  await page.getByRole("switch", { name: "Movies", exact: true }).click();
  await page.getByRole("switch", { name: "Fixture hub", exact: true }).click();
  await page.locator("summary").filter({ hasText: "Saved selections" }).click();
  await page
    .getByRole("button", { name: "Check saved sources", exact: true })
    .click();
  await expect(
    page.getByText(/No longer advertised by Plex; selection retained/).first(),
  ).toBeVisible();
  await page.locator("summary").filter({ hasText: "Saved selections" }).click();
  await page
    .getByRole("button", { name: "Customize", exact: true })
    .first()
    .click();
  await page.getByRole("spinbutton", { name: "Source item limit" }).fill("25");
  await page
    .getByRole("button", { name: "Preview candidates", exact: true })
    .click();
  await expect(
    page.getByText("Fixture Plex media", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Activity and history", exact: true })
    .click();
  await expect(page.getByRole("alertdialog")).toBeVisible();
  await page.getByRole("button", { name: "Keep editing", exact: true }).click();
  await page.evaluate(() => (window.__mock.external = true));
  await page
    .getByRole("button", { name: "Save Smart Prefetch", exact: true })
    .click();
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          window.__mock.calls.filter(
            (c) => c.path === "/api/v1/settings" && c.method === "PUT",
          ).length,
      ),
    )
    .toBe(1);
  const saved = await page.evaluate(
    () =>
      window.__mock.calls.find(
        (c) => c.path === "/api/v1/settings" && c.method === "PUT",
      ).data,
  );
  expect(saved.providers[0].name).toBe("External fixture provider");
  expect(saved.prefetch.connections_per_job).toBe(3);
  expect(saved.prefetch.plex.policy.movies).toBe(false);
  expect(
    saved.prefetch.plex.sources.find((s) => s.path === "/hubs/fixture").title,
  ).toBe("Fixture hub");
  expect(
    saved.prefetch.plex.sources.find((s) => s.path === "/hubs/fixture").limit,
  ).toBe(25);
  expect(
    saved.prefetch.plex.sources.some(
      (s) => s.path === "/hubs/fixture-unavailable",
    ),
  ).toBe(true);
  expect(
    await page.evaluate(
      () =>
        window.__mock.calls.filter(
          (c) => c.path === "/api/v1/prefetch" && c.method === "POST",
        ).length,
    ),
  ).toBe(0);
  await page.screenshot({
    path: info.outputPath("generated-smart-prefetch-settings.png"),
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
test("Plex library offers exact mapped source actions", async ({
  page,
}, info) => {
  await page.goto(fixture("plex"));
  await expect(
    page.getByRole("button", { name: "Open Fixture Series", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Movies", exact: true }).click();
  await page.getByRole("button", { name: "File table", exact: true }).click();
  await expect(
    page.getByRole("table", { name: "Library files" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Inspect file", exact: true }).click();
  await expect(
    page.getByText("8.0 MB · 4.0 MB cached", { exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Warm file", exact: true }).click();
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          window.__mock.calls.filter(
            (c) => c.path === "/api/v1/prefetch" && c.method === "POST",
          ).length,
      ),
    )
    .toBe(1);
  expect(
    await page.evaluate(
      () =>
        window.__mock.calls.find(
          (c) => c.path === "/api/v1/prefetch" && c.method === "POST",
        ).data.item,
    ),
  ).toBe("10000000-0000-0000-0000-000000000001");
  await page.screenshot({
    path: info.outputPath("generated-plex-library.png"),
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
test("activity and predictions remain separate from settings", async ({
  page,
}) => {
  await page.goto(fixture("activity"));
  await expect(
    page.getByRole("article", {
      name: "Warming file Fixture warming file",
      exact: true,
    }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Current warming" }),
  ).toBeVisible();
  await expect(
    page.getByRole("switch", { name: "Enable Smart Prefetch", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Predictions", exact: true }).click();
  await expect(
    page.getByText("Fixture prediction", { exact: true }),
  ).toBeVisible();
});

test("warming filters, grouped history and current coverage match v1 workflows", async ({
  page,
}, info) => {
  await page.goto(fixture("activity"));
  await expect(page.getByRole("article")).toHaveCount(20);
  if (!info.project.name.startsWith("360")) {
    const header = await page
      .getByText("Progress", { exact: true })
      .boundingBox();
    const row = await page
      .getByRole("progressbar", {
        name: "Attempt progress Fixture warming file",
        exact: true,
      })
      .boundingBox();
    expect(header && row && Math.abs(header.x - row.x)).toBeLessThan(2);
    const statusHeader = await page
      .getByText("Status", { exact: true })
      .boundingBox();
    const statusCell = await page
      .getByRole("article", {
        name: "Warming file Fixture warming file",
        exact: true,
      })
      .getByText("Queued", { exact: true })
      .boundingBox();
    expect(
      statusHeader && statusCell && Math.abs(statusHeader.x - statusCell.x),
    ).toBeLessThan(2);
  }

  await page.screenshot({
    path: `test-results/activity-overview-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByRole("article")).toHaveCount(6);
  await page
    .getByRole("searchbox", { name: "Search warming" })
    .fill("queued file 01");
  await expect(page.getByRole("article")).toHaveCount(1);
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("paused");
  await expect(page.getByRole("article")).toHaveCount(1);
  await page
    .getByRole("button", { name: "Clear filters", exact: true })
    .click();
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("deferred");
  await expect(page.getByRole("article")).toHaveCount(1);
  await page.getByRole("tab", { name: /Warming history/ }).click();
  await expect(page.getByRole("article")).toHaveCount(2);
  await expect(page.getByText("2 runs", { exact: true })).toBeVisible();
  await expect(
    page.getByRole("article").getByText("Fixture named hub", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("article").getByText("Fixture owner", { exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("fully-cached");
  await expect(page.getByRole("article")).toHaveCount(1);
  await expect(
    page.getByText("Fixture fully cached file", { exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("partial");
  await expect(page.getByRole("article")).toHaveCount(1);
  await expect(
    page.getByText("Fixture warming file", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("History type").selectOption("gaps");
  await expect(page.getByRole("article")).toHaveCount(2);
  await page
    .getByLabel("Filter outcome reason")
    .selectOption("Source unavailable");
  await expect(page.getByRole("article")).toHaveCount(1);
  await page.screenshot({
    path: `test-results/activity-${info.project.name}.png`,
    fullPage: true,
  });
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
});

test("predictions offer search, profile, source and current cache filters", async ({
  page,
}) => {
  await page.goto(fixture("activity"));
  await page.getByRole("button", { name: "Predictions", exact: true }).click();
  await page
    .getByRole("searchbox", { name: "Search predictions" })
    .fill("absent fixture");
  await expect(
    page.getByText("No predictions match these filters."),
  ).toBeVisible();
  await page
    .getByRole("searchbox", { name: "Search predictions" })
    .fill("Fixture prediction");
  await page
    .getByLabel("Filter prediction viewer")
    .selectOption("Fixture owner");
  await page
    .getByLabel("Filter prediction source")
    .selectOption("next_episode");
  await page
    .getByLabel("Filter prediction cache status")
    .selectOption("partial");
  await expect(
    page.getByText("Fixture prediction", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Filter prediction cache status").selectOption("fully");
  await expect(
    page.getByText("No predictions match these filters."),
  ).toBeVisible();
});

test("coverage filters keep changed revisions unknown and bound metadata concurrency", async ({
  page,
}) => {
  await page.goto(fixture("activity"));
  await expect(
    page.getByRole("heading", { name: "Current warming" }),
  ).toBeVisible();
  await page.evaluate(() => {
    const mock = (window as any).__mock;
    mock.staleCoverage = true;
    mock.historyOverride = Array.from({ length: 26 }, (_, i) => ({
      id: `60000000-0000-0000-0000-${String(i + 1).padStart(12, "0")}`,
      item: `40000000-0000-0000-0000-${String(i + 1).padStart(12, "0")}`,
      revision: "fixture-revision",
      size: 8000000,
      ranges: [{ start: 0, end: 8000000 }],
      priority: 50,
      intent: "10000000-0000-0000-0000-000000000001",
      family: "plex",
      state: "complete",
      created_at: 1700000000000,
      updated_at: 1700000000000,
      expires_at: 1800000000000,
      next_attempt: 0,
      run: 1,
      attempts: 0,
      verified_bytes: 8000000,
      source_bytes: 8000000,
      cached_bytes: 0,
      reason: null,
      automatic_scope: null,
      attribution: { playback: [], policy: null },
    }));
  });
  await page.getByRole("button", { name: "Refresh", exact: true }).click();
  await page.getByRole("tab", { name: /Warming history/ }).click();
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("fully-cached");
  await expect(page.getByText(/Checked 26 files/)).toBeVisible();
  await expect(page.getByRole("article")).toHaveCount(0);
  await page
    .getByLabel("Filter warming", { exact: true })
    .selectOption("unknown");
  await expect(page.getByRole("article")).toHaveCount(20);
  await expect
    .poll(() => page.evaluate(() => (window as any).__mock.coverageMax))
    .toBeLessThanOrEqual(4);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.getByRole("article")).toHaveCount(6);
  await expect(
    page.getByRole("button", { name: "Retry", exact: true }),
  ).toHaveCount(0);
});

test("native cache restores whole-catalogue filters, sorting, paging and range maps", async ({
  page,
}, info) => {
  await page.goto(fixture("cache"));
  await page
    .getByRole("heading", { name: "Original cache storage", exact: true })
    .waitFor();
  await page.getByRole("tab", { name: "Files", exact: true }).click();
  const rows = page
    .getByRole("table", { name: "Cached files", exact: true })
    .getByRole("row")
    .filter({ has: page.getByRole("cell") });
  await expect(rows).toHaveCount(4);
  await page
    .getByLabel("Filter coverage", { exact: true })
    .selectOption("complete");
  await expect(rows).toHaveCount(1);
  await expect(
    page.getByRole("row", { name: "Fixture complete file", exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter coverage", { exact: true })
    .selectOption("empty");
  await expect(rows).toHaveCount(1);
  await expect(
    page.getByRole("row", { name: "Fixture empty file", exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter coverage", { exact: true })
    .selectOption("held");
  await expect(rows).toHaveCount(2);
  const unknown = page.getByRole("row", {
    name: "Fixture unknown hold",
    exact: true,
  });
  await expect(
    unknown.getByRole("button", { name: "Pin", exact: true }),
  ).toBeDisabled();
  await expect(
    unknown.getByRole("button", { name: "Evict", exact: true }),
  ).toBeDisabled();
  await page
    .getByRole("button", { name: "Clear filters", exact: true })
    .click();
  await page.evaluate(() => {
    (window as any).__mock.cachePageSize = 2;
  });
  await page
    .getByRole("button", { name: "Refresh files", exact: true })
    .click();
  await expect(rows).toHaveCount(2);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(
    page.getByRole("row", { name: "Fixture complete file", exact: true }),
  ).toBeVisible();
  await page.getByLabel("Sort cached files").selectOption("coverage");
  await expect(rows.first()).toHaveAccessibleName("Fixture complete file");
  await page
    .getByLabel("Filter coverage", { exact: true })
    .selectOption("partial");
  await expect(rows).toHaveCount(2);
  await page
    .getByRole("searchbox", { name: "Search cached files" })
    .fill("Fixture cached file");
  await expect(rows).toHaveCount(1);
  await page.getByRole("button", { name: "Ranges", exact: true }).click();
  await expect(
    page.getByRole("img", { name: "1 cached ranges; 1 known gaps" }),
  ).toBeVisible();
  await page.getByText("Known gaps", { exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Verify cached sample", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: `test-results/cache-ranges-${info.project.name}.png`,
    fullPage: true,
  });
  await page.getByRole("button", { name: "Close ranges", exact: true }).click();
  await page.evaluate(() => {
    (window as any).__mock.cacheReadOnly = true;
  });
  await page
    .getByRole("button", { name: "Refresh files", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Pin", exact: true }),
  ).toBeDisabled();
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
  const last = await page.evaluate(() =>
    (window as any).__mock.calls
      .filter(
        (c: any) =>
          c.path === "/api/v1/native-cache/legacy" && c.method === "GET",
      )
      .at(-1),
  );
  expect(last.query.coverage).toBe("partial");
  expect(last.query.sort).toBe("coverage");
  expect(last.query.search).toBe("Fixture cached file");
});

test("native cache exposes preserved eviction history and service event filters", async ({
  page,
}) => {
  await page.goto(fixture("cache"));
  await page.getByRole("tab", { name: "Evictions", exact: true }).click();
  await expect(
    page.getByText("Fixture evicted file 3", { exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter eviction reason", { exact: true })
    .selectOption("age");
  await page
    .getByLabel("Filter eviction folder", { exact: true })
    .selectOption("fixture-folder-1");
  await expect(
    page.getByText("Fixture evicted file 1", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("Fixture evicted file 3", { exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("button", { name: "Clear history filters", exact: true })
    .click();
  await page.evaluate(() => {
    (window as any).__mock.evictionPageSize = 1;
  });
  await page
    .getByRole("button", { name: "Latest evictions", exact: true })
    .click();
  await expect(
    page.getByText("Fixture evicted file 3", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Older evictions", exact: true })
    .click();
  await expect(
    page.getByText("Fixture evicted file 2", { exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Filter eviction reason", { exact: true })
    .selectOption("age");
  await expect(
    page.getByText("Fixture evicted file 3", { exact: true }),
  ).toBeVisible();
  await page.getByLabel("Eviction history source").selectOption("current");
  await page
    .getByRole("searchbox", { name: "Search evictions" })
    .fill("fixture-service-generation");
  await page
    .getByLabel("Filter eviction reason", { exact: true })
    .selectOption("legacy:manual");
  await expect(
    page.getByRole("paragraph").filter({ hasText: /^Existing cache: manual$/ }),
  ).toBeVisible();
  await page
    .getByLabel("Filter eviction reason", { exact: true })
    .selectOption("legacy:policy");
  await expect(
    page.getByText("No eviction records match this view."),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      (window as any).__mock.calls.filter((c: any) => c.method === "POST"),
    ),
  ).toHaveLength(0);
});

test("native cache folder settings are compact, editable and respect environment locks", async ({
  page,
}, info) => {
  await page.goto(fixture("cache-settings"));
  await expect(
    page.getByRole("heading", { name: "Playback cache", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("progressbar", { name: /fixture-folder-\d cache capacity/ }),
  ).toHaveCount(4);
  await expect(
    page.getByRole("progressbar", {
      name: "fixture-folder-1 cache capacity",
      exact: true,
    }),
  ).toHaveAttribute("aria-valuenow", "42");
  await expect(
    page.getByRole("switch", { name: /Enable native cache/ }),
  ).toBeChecked();
  await expect(page.getByRole("switch", { name: /Recovery mode/ })).toHaveCount(
    0,
  );
  await expect(
    page.getByText("Eviction disabled", { exact: true }),
  ).toHaveCount(4);
  await page.screenshot({
    path: info.outputPath("generated-cache-settings-overview.png"),
    fullPage: true,
  });
  const summary = page
    .locator("summary")
    .filter({ hasText: "fixture-folder-1" });
  await expect(summary).toBeVisible();
  await expect(
    page.getByRole("textbox", { name: "Original folder 1 ID", exact: true }),
  ).toHaveCount(0);
  await summary.click();
  const quota = page.getByRole("spinbutton", {
    name: "Original folder 1 quota",
    exact: true,
  });
  await expect(quota).toHaveValue("100");
  await quota.fill("120");
  expect(
    await page.evaluate(
      () =>
        (window as any).__mock.settings.native_cache.legacy.write.folders[0]
          .quota_bytes,
    ),
  ).toBe(120000000000);
  expect(
    await page.evaluate(
      () => (window as any).__mock.settings.native_cache.legacy.folders[0].path,
    ),
  ).toBe("/fixture/cache1");
  await page.screenshot({
    path: `test-results/cache-settings-${info.project.name}.png`,
    fullPage: true,
  });
  await page.goto(fixture("cache-settings-locked"));
  await page.locator("summary").filter({ hasText: "fixture-folder-1" }).click();
  await expect(
    page.getByRole("spinbutton", {
      name: "Original folder 1 quota",
      exact: true,
    }),
  ).toBeDisabled();
  await expect(
    page.getByRole("switch", { name: /Use existing v1 cache/ }),
  ).toBeDisabled();
  await expect
    .poll(() =>
      page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
    )
    .toBe(true);
});

test("Plex library restores grouped browsing, filters and full-catalogue pagination", async ({
  page,
}, info) => {
  await page.goto(fixture("plex"));
  await expect(page.getByText("Indexed files", { exact: true })).toBeVisible();
  await page
    .getByRole("button", { name: "Open Fixture Series", exact: true })
    .click();
  await expect(
    page.getByRole("table", { name: "Library files" }),
  ).toBeVisible();
  await page.getByLabel("Season", { exact: true }).fill("2");
  await expect(
    page.getByText("31 matching files", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Clear filters", exact: true })
    .click();
  await page.getByRole("button", { name: "File table", exact: true }).click();
  await page.getByLabel("Rows per page", { exact: true }).selectOption("25");
  await expect(
    page.getByRole("row", { name: "Fixture episode 61", exact: true }),
  ).toHaveCount(0);
  await page.getByLabel("Search media and paths").fill("Fixture episode 61");
  await page
    .getByRole("button", { name: "Apply filters", exact: true })
    .click();
  await expect(
    page.getByRole("row", { name: "Fixture episode 61", exact: true }),
  ).toBeVisible();
  await page
    .getByLabel("Native Cache", { exact: true })
    .selectOption("unavailable");
  await expect(
    page.getByRole("row", { name: "Fixture episode 61", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Inspect file", exact: true }).click();
  await expect(
    page.getByText(
      "No exact imported mapping. External files remain available in Plex.",
    ),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Warm file", exact: true }),
  ).toHaveCount(0);
  await page
    .getByRole("button", { name: "Close file details", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Clear filters", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Grouped browse", exact: true })
    .click();
  await page.screenshot({
    path: info.outputPath("generated-plex-groups.png"),
    fullPage: true,
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test("cache settings keep changed folder identity separate from running storage", async ({
  page,
}) => {
  await page.goto(fixture("cache-settings"));
  const card = page.getByRole("article").filter({
    has: page.getByRole("heading", { name: "fixture-folder-1", exact: true }),
  });
  await expect(card.getByRole("progressbar")).toHaveAttribute(
    "aria-valuenow",
    "42",
  );
  await card
    .locator("summary")
    .filter({ hasText: "Configure fixture-folder-1" })
    .click();
  await card
    .locator("summary")
    .filter({ hasText: "Folder identity and paths" })
    .click();
  await card
    .getByRole("textbox", { name: "Original folder 1 path", exact: true })
    .fill("/fixture/changed-path");
  await expect(
    card.getByText("Usage unavailable", { exact: true }),
  ).toBeVisible();
  await expect(card.getByRole("progressbar")).toHaveCount(0);
  expect(
    await page.evaluate(
      () =>
        window.__mock.settings.native_cache.legacy.write.folders[0].eviction,
    ),
  ).toBeNull();
  expect(
    await page.evaluate(() =>
      window.__mock.calls.filter((c) => c.method !== "GET"),
    ),
  ).toEqual([]);
});

test("cache settings show unknown storage without hiding editable saved policy", async ({
  page,
}) => {
  await page.goto(fixture("cache-settings-unavailable"));
  await expect(
    page.getByText("Storage unavailable", { exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("Usage unavailable", { exact: true }),
  ).toHaveCount(4);
  await expect(page.getByRole("progressbar")).toHaveCount(0);
  await page
    .locator("summary")
    .filter({ hasText: "Configure fixture-folder-1" })
    .click();
  await expect(
    page.getByRole("spinbutton", {
      name: "Original folder 1 quota",
      exact: true,
    }),
  ).toHaveValue("100");
  await expect(
    page.getByRole("switch", { name: "Allow eviction", exact: true }),
  ).not.toBeChecked();
  expect(
    await page.evaluate(() =>
      window.__mock.calls.filter((c) => c.method !== "GET"),
    ),
  ).toEqual([]);
});

test("one Library keeps Plex filters and local root, group and page state", async ({
  page,
}, info) => {
  await page.goto(fixture("library-unified"));
  await expect(
    page.getByRole("heading", { name: "Library", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("heading", { level: 1 })).toHaveCount(1);
  await page.getByRole("button", { name: "Movies", exact: true }).click();
  await page.getByRole("button", { name: "File table", exact: true }).click();
  await page.getByLabel("Search media and paths").fill("Fixture movie");
  await page
    .getByRole("button", { name: "Apply filters", exact: true })
    .click();
  await page
    .getByRole("link", { name: "Plex connections", exact: true })
    .click();
  await expect(
    page.getByRole("link", {
      name: "Connections and path mappings",
      exact: true,
    }),
  ).toHaveAttribute("aria-current", "page");
  await page.getByRole("link", { name: "Browse media", exact: true }).click();
  await expect(page.getByLabel("Search media and paths")).toHaveValue(
    "Fixture movie",
  );
  await page.getByRole("link", { name: "Local files", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Fixture local-only group", exact: true }),
  ).toBeVisible();
  await page
    .getByRole("combobox", { name: "Library folder", exact: true })
    .click();
  await page
    .getByRole("option", { name: /^\/fixture\/local-secondary/ })
    .click();
  await page
    .getByRole("button", { name: "Fixture local-only group", exact: true })
    .click();
  await expect(
    page.getByRole("link", { name: /Fixture local file 001/ }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Next", exact: true }).click();
  const last = page.getByRole("link", { name: /Fixture local file 101/ });
  await expect(last).toBeVisible();
  await expect(last).toHaveAttribute("href", /\/explorer\?item=/);
  await page.screenshot({
    path: info.outputPath("generated-unified-local-files.png"),
    fullPage: true,
  });
  await page.getByRole("link", { name: "Browse media", exact: true }).click();
  await expect(page.getByLabel("Search media and paths")).toHaveValue(
    "Fixture movie",
  );
  await expect(
    page.getByRole("button", { name: "Movies", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(
    page.getByRole("table", { name: "Library files" }),
  ).toBeVisible();
  const before = await page.evaluate(
    () =>
      window.__mock.calls.filter((c) =>
        c.path.startsWith("/api/v1/library/catalogue"),
      ).length,
  );
  await page.waitForTimeout(5500);
  expect(
    await page.evaluate(
      () =>
        window.__mock.calls.filter((c) =>
          c.path.startsWith("/api/v1/library/catalogue"),
        ).length,
    ),
  ).toBe(before);
  await page.getByRole("link", { name: "Local files", exact: true }).click();
  await expect(
    page.getByRole("combobox", { name: "Library folder", exact: true }),
  ).toContainText("/fixture/local-secondary");
  await expect(
    page.getByRole("link", { name: /Fixture local file 101/ }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Previous", exact: true }).click();
  await expect(
    page.getByRole("link", { name: /Fixture local file 001/ }),
  ).toBeVisible();
  await page.evaluate(() => window.__mock.router.navigate(-1));
  await expect(page.getByLabel("Search media and paths")).toHaveValue(
    "Fixture movie",
  );
  await page.evaluate(() => window.__mock.router.navigate(1));
  await expect(
    page.getByRole("link", { name: /Fixture local file 001/ }),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      window.__mock.calls.filter((c) => c.method !== "GET"),
    ),
  ).toEqual([]);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test("Library defaults to local files without Plex and keeps explicit Plex setup available", async ({
  page,
}) => {
  await page.goto(fixture("library-no-plex"));
  await expect(
    page.getByRole("button", { name: "Fixture local-only group", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      window.__mock.calls.some((c) => c.path.includes("/browse")),
    ),
  ).toBe(false);
  await page.getByRole("link", { name: "Browse media", exact: true }).click();
  await expect(
    page.getByRole("link", {
      name: "Connect Plex and select a server",
      exact: true,
    }),
  ).toBeVisible();
  await page
    .getByRole("link", { name: "Connections and path mappings", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Connect Plex account", exact: true }),
  ).toBeVisible();
});

test("Plex failure does not block local-only files or trigger automatic scans", async ({
  page,
}) => {
  await page.goto(fixture("library-offline"));
  await expect(
    page.getByText("Fixture Plex unavailable", { exact: true }),
  ).toBeVisible();
  await page.getByRole("link", { name: "Local files", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Fixture local-only group", exact: true }),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      window.__mock.calls.filter((c) => c.method !== "GET"),
    ),
  ).toEqual([]);
  await page
    .getByRole("button", { name: "Refresh index", exact: true })
    .click();
  await expect
    .poll(() =>
      page.evaluate(
        () =>
          window.__mock.calls.filter(
            (c) =>
              c.path === "/api/v1/library/catalogue" && c.method === "POST",
          ).length,
      ),
    )
    .toBe(1);
});

test("old Plex bookmarks retain their filters and connection view", async ({
  page,
}) => {
  await page.goto(
    fixture("library-unified") +
      "&entry=" +
      encodeURIComponent(
        "/plex-library?category=movies&view=files&search=Fixture&server=fixture-server#retained",
      ),
  );
  await expect(
    page.getByRole("table", { name: "Library files" }),
  ).toBeVisible();
  expect(
    await page.evaluate(() => {
      const l = window.__mock.router.state.location;
      return {
        path: l.pathname,
        source: new URLSearchParams(l.search).get("source"),
        search: new URLSearchParams(l.search).get("search"),
        hash: l.hash,
      };
    }),
  ).toEqual({
    path: "/library",
    source: "plex",
    search: "Fixture",
    hash: "#retained",
  });
  await page.goto(
    fixture("library-unified") +
      "&entry=" +
      encodeURIComponent("/plex-library?tab=connections&server=fixture-server"),
  );
  await expect(
    page.getByRole("link", {
      name: "Connections and path mappings",
      exact: true,
    }),
  ).toHaveAttribute("aria-current", "page");
  expect(
    await page.evaluate(() =>
      new URLSearchParams(window.__mock.router.state.location.search).get(
        "server",
      ),
    ),
  ).toBe("fixture-server");
});

test("combined media includes local files without Plex matches by default", async ({
  page,
}, info) => {
  await page.goto(fixture("library-unified"));
  await expect(
    page.getByRole("button", { name: "All media", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(
    page.getByText("Fixture unindexed video", { exact: true }).first(),
  ).toBeVisible();
  await page.getByRole("button", { name: "Unmatched", exact: true }).click();
  await expect(
    page.getByText("Fixture unindexed video", { exact: true }).first(),
  ).toBeVisible();
  await expect(page.getByText("Fixture Series", { exact: true })).toHaveCount(
    0,
  );
  await page.screenshot({
    path: info.outputPath("generated-combined-media.png"),
    fullPage: true,
  });
  expect(
    await page.evaluate(() =>
      window.__mock.calls.filter((c) => c.method !== "GET"),
    ),
  ).toEqual([]);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
