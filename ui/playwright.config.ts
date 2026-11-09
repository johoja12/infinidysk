import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./tests",
  testMatch: "fork-ui-parity.spec.ts",
  fullyParallel: true,
  workers: 2,
  reporter: "line",
  use: {
    baseURL: "http://127.0.0.1:5188",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [360, 768, 1280].flatMap((width) =>
    (["light", "dark"] as const).map((colorScheme) => ({
      name: `${width}-${colorScheme}`,
      use: { viewport: { width, height: 900 }, colorScheme },
    })),
  ),
  webServer: {
    command: "npm run dev -- --host 127.0.0.1 --port 5188",
    url: "http://127.0.0.1:5188",
    reuseExistingServer: !process.env.CI,
  },
});
