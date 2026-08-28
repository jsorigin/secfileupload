import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: ".",
  testMatch: "uploader.spec.ts",
  fullyParallel: false,
  retries: 0,
  reporter: "line",
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure"
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] }
    }
  ],
  webServer: [
    {
      command:
        "powershell -NoProfile -Command \"$env:ASPNETCORE_URLS='http://127.0.0.1:5080'; $env:AllowedOrigins__Origins__0='http://127.0.0.1:4173'; dotnet run --no-build --project ..\\..\\src\\SecureUpload.Web\\SecureUpload.Web.csproj\"",
      url: "http://127.0.0.1:5080/upload",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000
    },
    {
      command: "node host-server.mjs",
      url: "http://127.0.0.1:4173",
      reuseExistingServer: !process.env.CI,
      timeout: 30_000
    }
  ]
});
