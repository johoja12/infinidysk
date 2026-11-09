import { test, expect } from "@playwright/test";
const fixture = (screen: string) =>
  `/tests/fork-ui-parity.html?screen=${screen}`;
test("fork feature navigation is visible", async ({ page }) => {
  await page.goto(fixture("nav"));
  for (const label of ["Native Cache", "Plex Library", "Smart Prefetch"])
    await expect(
      page.getByRole("link", { name: label, exact: true }),
    ).toBeVisible();
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
  await expect(page.getByText(/12 verified cached block reads/)).toBeVisible();
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
    page.getByText("Fixture Plex media", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Cache coverage", exact: true })
    .click();
  await expect(page.getByText("4.0 MB cached", { exact: true })).toBeVisible();
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
    page.getByRole("cell", { name: /Fixture warming file/ }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "Warming history" }),
  ).toBeVisible();
  await expect(
    page.getByRole("switch", { name: "Enable Smart Prefetch", exact: true }),
  ).toHaveCount(0);
  await page.getByRole("button", { name: "Predictions", exact: true }).click();
  await expect(
    page.getByText("Fixture prediction", { exact: true }),
  ).toBeVisible();
});
