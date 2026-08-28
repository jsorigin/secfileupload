import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Frame, type Page } from "@playwright/test";

const stableId = "a".repeat(64);

async function uploader(page: Page, path = "/"): Promise<Frame> {
  await page.goto(path);
  const frame = page.frame({ url: /127\.0\.0\.1:5080\/upload\?parentOrigin=/ });
  expect(frame).not.toBeNull();
  await expect(frame!.getByRole("heading", { name: "Secure file upload" })).toBeVisible();
  return frame!;
}

async function mockAcceptedUpload(page: Page, status = "pending") {
  await page.route("**/api/uploads", async route => {
    if (route.request().method() !== "POST") {
      return route.continue();
    }
    await route.fulfill({
      status: 202,
      contentType: "application/json",
      body: JSON.stringify({ fileId: stableId, status })
    });
  });
}

test("approved host with suppressed referrer receives exact-origin accepted messages", async ({ page }) => {
  await mockAcceptedUpload(page);
  const frame = await uploader(page);

  await frame.getByLabel("Choose one file").setInputFiles({
    name: "report.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("browser fixture")
  });

  await frame.getByRole("button", { name: "Upload file" }).click();

  await expect(frame.getByText("The file is pending a security check.")).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as any).receivedMessages)).toEqual([
    { version: 1, type: "secure-upload", fileId: stableId, status: "accepted" },
    { version: 1, type: "secure-upload", fileId: stableId, status: "pending" }
  ]);
});

test("unapproved parentOrigin allows upload UI but sends no parent messages", async ({ page }) => {
  await mockAcceptedUpload(page);
  const frame = await uploader(page, "/unapproved-parameter");

  await frame.getByLabel("Choose one file").setInputFiles({
    name: "report.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("browser fixture")
  });
  await frame.getByRole("button", { name: "Upload file" }).click();

  await expect(frame.getByText("The file is pending a security check.")).toBeVisible();
  await expect.poll(() => page.evaluate(() => (window as any).receivedMessages)).toEqual([]);
});

for (const terminal of [
  ["available", "File is available"],
  ["rejected", "File was rejected"],
  ["scan-error", "Security check could not finish"]
] as const) {
  test(`polling announces and posts ${terminal[0]}`, async ({ page }) => {
    await mockAcceptedUpload(page);
    await page.route(`**/api/uploads/${stableId}/status`, route => route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ fileId: stableId, status: terminal[0] })
    }));
    const frame = await uploader(page);

    await frame.getByLabel("Choose one file").setInputFiles({
      name: "report.txt",
      mimeType: "text/plain",
      buffer: Buffer.from("browser fixture")
    });
    await frame.getByRole("button", { name: "Upload file" }).click();

    await expect(frame.getByText(terminal[1])).toBeVisible({ timeout: 7_000 });
    await expect.poll(() => page.evaluate(
      status => (window as any).receivedMessages.some((message: any) => message.status === status),
      terminal[0])).toBe(true);
  });
}

test("unapproved host is rejected by frame-ancestors", async ({ page }) => {
  await page.goto("http://127.0.0.1:4174");
  await expect(page.getByRole("heading", { name: "Unapproved host" })).toBeVisible();
  await expect.poll(() => page.frames().some(frame =>
    frame.url().startsWith("http://127.0.0.1:5080/upload"))).toBe(false);
});

test("keyboard workflow, live announcements, retry, and focus recovery are accessible", async ({ page }) => {
  await page.route("**/api/uploads", route => route.fulfill({
    status: 503,
    contentType: "application/problem+json",
    body: JSON.stringify({ title: "Uploads are temporarily unavailable." })
  }));
  const frame = await uploader(page);

  await frame.getByLabel("Choose one file").setInputFiles({
    name: "retry.txt",
    mimeType: "text/plain",
    buffer: Buffer.from("retry")
  });
  await frame.getByLabel("Choose one file").focus();
  await page.keyboard.press("Tab");
  await page.keyboard.press("Enter");

  const liveRegion = frame.locator("#status-panel");
  await expect(liveRegion).toHaveAttribute("aria-live", "polite");
  await expect(frame.getByText("Uploads are temporarily unavailable.")).toBeVisible();
  await frame.getByRole("button", { name: "Choose another file" }).click();
  await expect(frame.getByLabel("Choose one file")).toBeFocused();
  await expect(frame.getByText("Select one supported document or image.")).toBeVisible();

  const results = await new AxeBuilder({ page })
    .include("iframe#uploader")
    .analyze();
  expect(results.violations).toEqual([]);
});

test("only an approved parent message changes light and dark themes", async ({ page }) => {
  const frame = await uploader(page);
  await expect(frame.locator("html")).toHaveAttribute("data-theme", "light");

  await page.getByRole("button", { name: "Dark theme" }).click();
  await expect(frame.locator("html")).toHaveAttribute("data-theme", "dark");

  await frame.evaluate(() =>
    window.postMessage({ type: "secure-upload-theme", theme: "light" }, window.origin));
  await expect(frame.locator("html")).toHaveAttribute("data-theme", "dark");

  await page.getByRole("button", { name: "Light theme" }).click();
  await expect(frame.locator("html")).toHaveAttribute("data-theme", "light");
});

test("narrow viewport keeps controls visible and usable", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 700 });
  const frame = await uploader(page);
  const box = await frame.locator("main.uploader").boundingBox();

  expect(box).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.width).toBeLessThanOrEqual(320);
  await expect(frame.getByRole("button", { name: "Upload file" })).toBeVisible();
  await expect(frame.getByLabel("Choose one file")).toBeVisible();
});
