#!/usr/bin/env node
/**
 * Synthetic browser acceptance for #144. Never contacts a control plane/provider.
 * Run from the repository root with Node 22:
 *   PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs \
 *     node scripts/workspace-browser-acceptance.mjs
 * Optional: BROWSER_EXECUTABLE, ACCEPTANCE_PORT (default 3288).
 * Requires the dashboard's existing dependencies and an installed Playwright browser.
 * A separate Playwright install avoids changing shared dashboard dependencies.
 * On unsupported ARM Ubuntu versions, use a compatible Playwright host override
 * (this run used PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-arm64).
 * Output: ignored .local/artifacts/issue138/browser/{desktop,narrow}/*.png,
 * report.json, requests.json. Approved reference PNGs are never overwritten.
 */
import assert from "node:assert/strict";
import { createServer } from "node:net";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";
import { mkdir, writeFile, rm } from "node:fs/promises";
import { resolve, join, relative } from "node:path";
import { pathToFileURL, fileURLToPath } from "node:url";

const root = resolve(fileURLToPath(new URL("..", import.meta.url)));
const artifacts = join(root, ".local/artifacts/issue138/browser");
const port = Number(process.env.ACCEPTANCE_PORT ?? 3288);
assert(Number.isInteger(port) && port > 1024 && port < 65536, "Invalid acceptance port");
const origin = `http://127.0.0.1:${port}`;
execFileSync("git", ["check-ignore", ".local/artifacts/issue138/browser/report.json"], { cwd: root });
await mkdir(artifacts, { recursive: true });
const scratch = join(artifacts, "scratch");
await mkdir(scratch, { recursive: true });
process.env.TMPDIR = scratch;
process.env.XDG_CACHE_HOME = join(scratch, "xdg-cache");
process.env.XDG_CONFIG_HOME = join(scratch, "xdg-config");
const report = {
  scope: "Local synthetic API browser acceptance only; not hosted/provider qualification",
  environment: { node: process.version, platform: process.platform, architecture: process.arch },
  startedAt: new Date().toISOString(), checks: [], screenshots: [], errors: [],
  gaps: ["No live provider, billing, retained-volume durability, gateway, T3 terminal or Git push qualification.",
    "Organization switching has no selector in the current production shell; late workspace-route replies are tested instead.",
    "Screenshots are drawing-review evidence, not pixel-perfect or cross-browser qualification."],
};
const requests = [];
let browser;
let server;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const check = async (name, action) => {
  await action();
  report.checks.push(name);
  console.log(`PASS ${name}`);
};
const until = async (predicate, description) => {
  const deadline = Date.now() + 12000;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await sleep(50);
  }
  throw new Error(`Timed out: ${description}`);
};
const timestamp = "2026-11-16T12:00:00Z";
function vm(id, overrides = {}) {
  return {
    id, kind: "primary", generation: 7, desiredState: "stopped", phase: "stopped",
    imageId: "image-standard", profile: "Standard", planId: "plan-cx23",
    providerAccountId: "account-fixture", region: "fsn1", sku: "cx23",
    pushAuthorized: false, ignoredDataAcknowledged: false, gitStatus: "not_required",
    computeHourlyRate: 0.008, estimatedMonthlyCompute: 5.84, computeReserved: false,
    lastActivityAt: timestamp, revision: 1, ...overrides,
  };
}
function workspace(id, name, overrides = {}) {
  return {
    id, name, organizationId: "org-fixture", imageId: "image-standard", profile: "Standard",
    planId: "plan-cx23", providerAccountId: "account-fixture", region: "fsn1", sku: "cx23",
    volumeSizeGb: 40, desiredState: "stopped", phase: "stopped", generation: 7,
    providerVolumeId: `volume-${id}`, volumeStatus: "retained", primaryVmId: `${id}-primary`,
    estimatedMonthlyCompute: 5.84, estimatedMonthlyStorage: 1.76, currency: "EUR",
    pricingUpdatedAt: timestamp, revision: 1, createdAt: timestamp, updatedAt: timestamp,
    lastActivityAt: timestamp, vms: [vm(`${id}-primary`)], ...overrides,
  };
}
const selection = {
  imageId: "image-standard", imageName: "Qualified development image",
  profile: "Standard", planId: "plan-cx23", planName: "Development",
  providerAccountName: "Synthetic Hetzner", region: "fsn1", sku: "cx23",
};
function fixture() {
  return {
    role: "owner", workspaces: [
      workspace("alpha", "api-sandbox"),
      workspace("beta", "docs-preview", { vms: [vm("beta-primary", { phase: "ready", desiredState: "running", computeReserved: true })] }),
      workspace("gamma", "dashboard-refactor", {
        vms: [vm("gamma-primary", { phase: "provisioning", desiredState: "running", computeReserved: true })],
      }),
      workspace("delta", "site-recovery", {
        vms: [vm("delta-primary"), vm("delta-task", {
          kind: "ephemeral", phase: "awaiting_git_save", gitStatus: "push_failed", computeReserved: true,
          errorCode: "git_push_failed", workBranch: "minicloud/site-repair",
          repository: "https://example.invalid/synthetic/site.git",
        })],
      }),
      workspace("epsilon", "release-tools"),
    ],
    selections: { available: true, options: [selection] },
    selectionFailure: false, readFailure: false, ambiguousCreate: false,
    staleMutation: false, launchExpired: false, launchTtlMs: 60000,
    sequence: 0, delayedDetail: null, delayedLaunch: null,
    unexpected: [], external: [], calls: [],
  };
}
async function installApi(context, state) {
  await context.route("**/*", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin === origin && !url.pathname.startsWith("/api/")) return route.continue();
    if (!["http://localhost:3210", origin].includes(url.origin)) {
      state.external.push(url.href);
      return route.abort("blockedbyclient");
    }
    const method = request.method();
    const body = request.postDataJSON();
    const entry = { method, path: url.pathname, query: Object.fromEntries(url.searchParams), body };
    if (method !== "OPTIONS") { state.calls.push(entry); requests.push(entry); }
    const fulfill = (data, status = 200) => route.fulfill({
      status, contentType: "application/json",
      headers: { "Access-Control-Allow-Origin": origin, "Access-Control-Allow-Credentials": "true",
        "Access-Control-Allow-Headers": "content-type,x-minicloud-csrf", "Access-Control-Allow-Methods": "GET,POST,PUT,OPTIONS" },
      body: JSON.stringify(data),
    });
    const error = (code, message, status = 409) => fulfill({ error: { code, message, details: [] } }, status);
    if (method === "OPTIONS") return fulfill({});
    if (url.pathname === "/api/account/capabilities") return fulfill({ password: true, google: false, emailRecovery: false, bootstrapState: "ready" });
    if (url.pathname === "/api/account/session") return fulfill({
      userId: "user-fixture", csrfToken: "synthetic-csrf", expiresAt: "2099-01-01T00:00:00Z", idleExpiresAt: "2099-01-01T00:00:00Z",
    });
    if (url.pathname === "/api/me") return fulfill({
      id: "user-fixture", email: "acceptance@example.invalid", displayName: "Browser Acceptance",
      organizations: [{ id: "org-fixture", name: "Synthetic organization", slug: "synthetic", role: state.role }],
    });
    if (url.pathname === "/api/development-workspaces/selections") {
      if (state.selectionFailure) return error("catalog_unavailable", "Synthetic catalog unavailable", 503);
      return fulfill(state.selections);
    }
    if (url.pathname === "/api/development-workspaces") {
      if (method === "GET") return state.readFailure
        ? error("read_failed", "Synthetic refresh failed", 500) : fulfill(state.workspaces);
      assert.equal(method, "POST");
      assert.equal(body.organizationId, "org-fixture");
      assert(body.idempotencyKey);
      if (state.ambiguousCreate) { state.ambiguousCreate = false; return route.abort("failed"); }
      const created = workspace("created", body.name, { volumeSizeGb: body.volumeSizeGb });
      state.workspaces.push(created);
      return fulfill(created, 201);
    }
    const match = url.pathname.match(/^\/api\/development-workspaces\/([^/]+)(?:\/(.*))?$/);
    if (match) {
      const item = state.workspaces.find(item => item.id === match[1]);
      if (!item) return error("not_found", "Workspace not found", 404);
      if (!match[2]) {
        assert.equal(method, "GET");
        const snapshot = structuredClone(item);
        if (state.delayedDetail?.id === item.id) {
          state.delayedDetail.requested = true;
          await state.delayedDetail.promise;
        }
        return state.readFailure ? error("read_failed", "Synthetic refresh failed", 500) : fulfill(snapshot);
      }
      assert.equal(method, "POST");
      assert.equal(url.searchParams.get("organizationId"), "org-fixture");
      if (match[2] === "stop-all") {
        for (const target of item.vms) {
          target.desiredState = "stopped";
          target.phase = target.kind === "ephemeral" ? "awaiting_git_save" : "stopped";
          target.gitStatus = target.kind === "ephemeral" ? "push_failed" : "not_required";
          target.errorMessage = target.kind === "ephemeral" ? "Repository push rejected. Repair repository authorization." : null;
        }
        return fulfill(item);
      }
      if (match[2] === "vms/ephemeral") {
        assert(body.pushAuthorized && body.acknowledgeIgnoredDataLoss && body.idempotencyKey);
        const id = `task-${++state.sequence}`;
        item.vms.push(vm(id, { ...body, kind: "ephemeral", desiredState: "running", phase: "ready",
          generation: 1, gitStatus: "clean", ignoredDataAcknowledged: true,
          computeReserved: true, workBranch: `minicloud/${id}`, repository: body.repository }));
        return fulfill(item, 201);
      }
      const mutation = match[2].match(/^vms\/([^/]+)\/(start|stop|discard|launch)$/);
      if (mutation) {
        const target = item.vms.find(vm => vm.id === mutation[1]);
        assert(target, "Unknown fixture VM");
        if (state.staleMutation) { state.staleMutation = false; return error("stale_generation", "Generation changed. Refresh workspace status."); }
        assert.equal(body.generation, target.generation);
        const action = mutation[2];
        if (action === "launch") {
          const response = {
            url: "https://gateway.example.invalid/synthetic-session",
            expiresAt: new Date(Date.now() + (state.launchExpired ? -1000 : state.launchTtlMs)).toISOString(),
          };
          if (state.delayedLaunch) {
            state.delayedLaunch.requested = true;
            await state.delayedLaunch.promise;
          }
          const result = await fulfill(response);
          if (state.delayedLaunch) state.delayedLaunch.settled = true;
          return result;
        }
        if (action === "start") { target.desiredState = "running"; target.phase = "provisioning"; target.computeReserved = true; }
        if (action === "stop") { target.desiredState = "stopped"; target.phase = "stopped"; target.gitStatus = "saved"; target.computeReserved = false; target.errorMessage = null; }
        if (action === "discard") {
          assert.equal(target.kind, "ephemeral");
          assert.equal(body.discardLocalData, true);
          assert.equal(body.acknowledgeIgnoredDataLoss, true);
          target.phase = "stopped"; target.desiredState = "stopped"; target.gitStatus = "discarded"; target.computeReserved = false;
        }
        target.revision++;
        return fulfill(item);
      }
    }
    state.unexpected.push(entry);
    return error("unexpected_fixture_request", `${method} ${url.pathname}`, 500);
  });
}
const mutations = state => state.calls.filter(call => call.method !== "GET");
async function choose(dialog) {
  await dialog.getByLabel("Workspace image", { exact: true }).selectOption(selection.imageId);
  await dialog.getByLabel("Profile", { exact: true }).selectOption(selection.profile);
  await dialog.getByLabel("Approved plan", { exact: true }).selectOption(selection.planId);
}
async function capture(page, name, viewport) {
  const directory = join(artifacts, viewport);
  await mkdir(directory, { recursive: true });
  const path = join(directory, `${name}.png`);
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path, fullPage: !name.startsWith("create"), animations: "disabled" });
  report.screenshots.push(relative(root, path));
}
async function refresh(page) {
  await page.getByRole("button", { name: "Refresh workspace status", exact: true }).click();
  await until(() => page.getByRole("button", { name: "Refresh workspace status", exact: true }).isEnabled(), "refresh settled");
}
async function runViewport(name, viewport) {
  const context = await browser.newContext({ viewport, serviceWorkers: "block", acceptDownloads: false, colorScheme: "light" });
  const state = fixture();
  await installApi(context, state);
  const page = await context.newPage();
  page.setDefaultTimeout(12000);
  page.on("pageerror", error => report.errors.push(`${name}: ${error.message}`));
  const card = id => page.getByRole("article", { name: `VM ${id}`, exact: true });
  try {
    await page.goto(`${origin}/workspaces`);
    await check(`${name}: collection/filter/detail/back are read-only`, async () => {
      await page.getByRole("table", { name: "Workspaces", exact: true }).waitFor();
      await capture(page, "collection", name);
      const filterTrigger = page.locator('[aria-label="workspaces controls"] button[aria-haspopup="dialog"]');
      await filterTrigger.click();
      await page.getByRole("combobox", { name: "Primary VM state", exact: true }).click();
      await page.getByRole("option", { name: "Stopped", exact: true }).click();
      await page.keyboard.press("Escape");
      await until(async () => await page.getByRole("link", { name: "docs-preview", exact: true }).count() === 0, "primary-state filter applied");
      const search = page.getByRole("textbox", { name: /Search workspaces/i });
      await search.fill("api-sandbox");
      await until(async () => await page.getByRole("link", { name: "docs-preview", exact: true }).count() === 0, "filter applied");
      await page.getByRole("link", { name: "api-sandbox", exact: true }).click();
      await card("alpha-primary").waitFor();
      await page.getByRole("link", { name: "Workspaces", exact: true }).last().click();
      await until(async () => await search.inputValue() === "api-sandbox", "filter persisted");
      assert.equal(mutations(state).length, 0);
      await search.fill("");
      await filterTrigger.click();
      await page.getByRole("combobox", { name: "Primary VM state", exact: true }).click();
      await page.getByRole("option", { name: "All", exact: true }).click();
      await page.keyboard.press("Escape");
      await page.getByRole("link", { name: "docs-preview", exact: true }).waitFor();
    });
    await check(`${name}: dialog keyboard focus returns to trigger`, async () => {
      const trigger = page.getByRole("button", { name: "Create workspace", exact: true });
      await trigger.focus(); await page.keyboard.press("Enter");
      const dialog = page.getByRole("dialog", { name: "Create workspace", exact: true });
      await dialog.waitFor();
      assert(await dialog.evaluate(el => el.contains(document.activeElement)));
      for (let i = 0; i < 12; i++) {
        await page.keyboard.press("Tab");
        assert(await dialog.evaluate(el => el.contains(document.activeElement)), "Focus escaped modal");
      }
      await page.keyboard.press("Escape");
      await until(() => trigger.evaluate(el => el === document.activeElement), "focus returned");
    });
    await check(`${name}: create stopped, ambiguous retry keeps idempotency key`, async () => {
      await page.getByRole("button", { name: "Create workspace", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Create workspace", exact: true });
      await dialog.getByLabel("Workspace name", { exact: true }).fill("browser-created");
      await choose(dialog);
      await capture(page, "create", name);
      const submit = dialog.getByRole("button", { name: "Create stopped workspace", exact: true });
      await submit.scrollIntoViewIfNeeded();
      const bounds = await submit.boundingBox();
      assert(bounds && bounds.y >= 0 && bounds.y + bounds.height <= viewport.height, "Create action is clipped");
      if (name === "narrow") await capture(page, "create-actions", name);
      state.ambiguousCreate = true;
      await submit.click();
      await dialog.getByRole("alert").waitFor();
      await dialog.getByRole("button", { name: "Create stopped workspace", exact: true }).click();
      await until(async () => await dialog.count() === 0, "created dialog closed");
      const creates = mutations(state).filter(call => call.path === "/api/development-workspaces");
      assert.equal(creates.length, 2);
      assert.deepEqual(creates[0].body, creates[1].body);
      assert.equal(state.workspaces.find(item => item.id === "created").vms[0].phase, "stopped");
      assert.equal(mutations(state).filter(call => /\/start$/.test(call.path)).length, 0);
    });
    await page.getByRole("link", { name: "browser-created", exact: true }).last().click();
    await card("created-primary").waitFor();
    await check(`${name}: explicit start, readiness, T3 and preview authorization, expired links`, async () => {
      const primary = card("created-primary");
      assert.equal(await primary.getByRole("button", { name: "Connect to T3", exact: true }).count(), 0);
      await primary.getByRole("button", { name: "Start compute", exact: true }).click();
      await until(() => primary.innerText().then(text => text.includes("provisioning")), "provisioning shown");
      assert.equal(await primary.getByRole("button", { name: "Connect to T3", exact: true }).count(), 0);
      const vm = state.workspaces.find(item => item.id === "created").vms[0];
      vm.phase = "ready"; vm.revision++;
      await refresh(page);
      await primary.getByRole("button", { name: "Connect to T3", exact: true }).click();
      await primary.getByRole("link", { name: /Open authorized T3 session/ }).waitFor();
      const beforePreview = mutations(state).length;
      await primary.getByRole("button", { name: "Open preview", exact: true }).click();
      assert.equal(mutations(state).length, beforePreview, "Opening preview fields must not authorize or start compute");
      await primary.getByLabel("Registered preview slug").fill("docs");
      await primary.getByRole("button", { name: "Authorize preview", exact: true }).click();
      await primary.getByRole("link", { name: /Open authorized docs preview/ }).waitFor();
      const launch = mutations(state).filter(call => call.path.endsWith("/launch"));
      assert.deepEqual(launch.map(call => call.body), [{ generation: 7, service: "t3" }, { generation: 7, service: "docs" }]);
      state.launchExpired = true;
      await primary.getByRole("button", { name: "Connect to T3", exact: true }).click();
      await primary.getByRole("alert").waitFor();
      assert.equal(await primary.getByRole("link", { name: /Open authorized/ }).count(), 0);
      state.launchExpired = false;
      state.launchTtlMs = 1000;
      await primary.getByRole("button", { name: "Connect to T3", exact: true }).click();
      await primary.getByRole("link", { name: /Open authorized T3 session/ }).waitFor();
      await until(async () => await primary.getByRole("link", { name: /Open authorized/ }).count() === 0, "expired link removed");
      state.launchTtlMs = 60000;
    });
    await check(`${name}: late launch authorization cannot cross VM generations`, async () => {
      let release;
      state.delayedLaunch = { requested: false, settled: false, promise: new Promise(resolve => { release = resolve; }) };
      await card("created-primary").getByRole("button", { name: "Connect to T3", exact: true }).click();
      await until(() => state.delayedLaunch.requested, "delayed launch requested");
      const vm = state.workspaces.find(item => item.id === "created").vms[0];
      vm.generation++; vm.revision++;
      await refresh(page);
      release();
      await until(() => state.delayedLaunch.settled, "late launch settled");
      assert.equal(await card("created-primary").getByRole("link", { name: /Open authorized/ }).count(), 0);
      state.delayedLaunch = null;
    });
    await check(`${name}: stop and revisit do not restart compute`, async () => {
      await card("created-primary").getByText("Advanced VM controls", { exact: true }).click();
      await card("created-primary").getByRole("button", { name: "Stop compute", exact: true }).click();
      await card("created-primary").getByText("Stopped", { exact: true }).waitFor();
      const count = mutations(state).length;
      await page.reload(); await card("created-primary").waitFor();
      assert.equal(mutations(state).length, count);
    });
    await check(`${name}: independent sibling launches require both acknowledgments`, async () => {
      for (let i = 0; i < 2; i++) {
        await page.getByRole("button", { name: "Launch ephemeral VM", exact: true }).click();
        const dialog = page.getByRole("dialog", { name: "Launch ephemeral VM", exact: true });
        await choose(dialog);
        await dialog.getByLabel("Repository URL", { exact: true }).fill("https://example.invalid/synthetic/repository.git");
        await dialog.getByLabel("Base ref", { exact: true }).fill("refs/heads/main");
        await dialog.getByLabel("Resolved base commit SHA", { exact: true }).fill("a".repeat(40));
        const launch = dialog.getByRole("button", { name: "Launch ephemeral VM", exact: true });
        assert(await launch.isDisabled());
        await dialog.getByLabel(/I authorize task-scoped pushes/).check();
        assert(await launch.isDisabled());
        await dialog.getByLabel(/I acknowledge ignored\/untracked/).check();
        await launch.click();
        await until(async () => await dialog.count() === 0, "ephemeral launch completed");
      }
      const item = state.workspaces.find(item => item.id === "created");
      assert.equal(item.vms.length, 3);
      assert.equal(item.vms[0].phase, "stopped");
      assert.equal(item.providerVolumeId, "volume-created");
      assert.notEqual(item.vms[1].workBranch, item.vms[2].workBranch);
      await card("created-primary").getByRole("button", { name: "Start compute", exact: true }).click();
      await card("created-primary").getByText("provisioning", { exact: true }).waitFor();
      item.vms[0].phase = "ready"; item.vms[0].revision++;
      await refresh(page);
      await capture(page, "detail", name);
    });
    await check(`${name}: stop-all cancel then confirm lists all generations`, async () => {
      const before = mutations(state).length;
      await page.getByRole("button", { name: "Stop all VMs", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Stop all VMs?", exact: true });
      for (const id of ["created-primary", "task-1", "task-2"]) assert((await dialog.innerText()).includes(id));
      await dialog.getByRole("button", { name: "Cancel stop all", exact: true }).click();
      assert.equal(mutations(state).length, before);
      await page.getByRole("button", { name: "Stop all VMs", exact: true }).click();
      await dialog.getByRole("button", { name: "Confirm stop all", exact: true }).click();
      await until(async () => await dialog.count() === 0, "stop all completed");
      assert.equal(mutations(state).at(-1).path, "/api/development-workspaces/created/stop-all");
    });
    await check(`${name}: Git-save repair/retry and generation-bound discard isolate siblings/volume`, async () => {
      await page.getByRole("button", { name: "Expand VM task-1", exact: true }).click();
      const item = state.workspaces.find(item => item.id === "created");
      const primarySnapshot = structuredClone(item.vms[0]);
      const volumeSnapshot = [item.providerVolumeId, item.volumeSizeGb, item.volumeStatus];
      await card("task-1").getByRole("region", { name: "Git save recovery for VM task-1" }).waitFor();
      await capture(page, "recovery", name);
      await page.getByRole("button", { name: "Expand VM task-2", exact: true }).click();
      const siblingSnapshot = structuredClone(item.vms[2]);
      await card("task-1").getByRole("button", { name: "Retry stop", exact: true }).click();
      await until(() => card("task-1").getByRole("button", { name: "Start compute", exact: true }).isEnabled(), "Git-save retry settled");
      assert.deepEqual(item.vms[2], siblingSnapshot);
      const savedSiblingSnapshot = structuredClone(item.vms[1]);
      await card("task-2").getByRole("button", { name: "Discard local data…", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Discard VM task-2 generation 1", exact: true });
      const confirm = dialog.getByRole("button", { name: "Confirm irreversible discard", exact: true });
      assert(await confirm.isDisabled());
      await dialog.getByLabel("Type discard task-2 1", { exact: true }).fill("discard task-2 0");
      await dialog.getByRole("checkbox").check();
      assert(await confirm.isDisabled());
      await dialog.getByLabel("Type discard task-2 1", { exact: true }).fill("discard task-2 1");
      await confirm.click();
      await until(async () => await dialog.count() === 0, "discard completed");
      assert.deepEqual(mutations(state).at(-1).body, { generation: 1, discardLocalData: true, acknowledgeIgnoredDataLoss: true });
      assert.deepEqual(item.vms[0], primarySnapshot);
      assert.deepEqual(item.vms[1], savedSiblingSnapshot);
      assert.deepEqual([item.providerVolumeId, item.volumeSizeGb, item.volumeStatus], volumeSnapshot);
    });
    await check(`${name}: stale generation and refresh failure surface actionable errors`, async () => {
      state.staleMutation = true;
      await card("created-primary").getByRole("button", { name: "Start compute", exact: true }).click();
      await card("created-primary").getByRole("alert").waitFor();
      assert((await card("created-primary").innerText()).includes("Generation changed"));
      state.readFailure = true;
      await refresh(page);
      await page.getByRole("alert").filter({ hasText: "Synthetic refresh failed" }).waitFor();
      state.readFailure = false;
      await page.getByRole("button", { name: "Retry", exact: true }).click();
      await until(async () => await page.getByRole("alert").filter({ hasText: "Synthetic refresh failed" }).count() === 0, "read retry");
    });
    await page.goto(`${origin}/workspaces`);
    await check(`${name}: eligibility unavailable/empty/failure refresh gates create`, async () => {
      state.selections = { available: false, unavailableReason: "Operator disabled fixture provisioning", options: [] };
      await page.getByRole("button", { name: "Create workspace", exact: true }).click();
      const dialog = page.getByRole("dialog", { name: "Create workspace", exact: true });
      await dialog.getByText("Operator disabled fixture provisioning").waitFor();
      const submit = dialog.getByRole("button", { name: "Create stopped workspace", exact: true });
      assert(await submit.isDisabled());
      state.selections = { available: true, options: [] };
      await dialog.getByRole("button", { name: "Refresh choices", exact: true }).click();
      await dialog.getByText(/No compatible choices/).waitFor();
      assert(await submit.isDisabled());
      state.selectionFailure = true;
      await dialog.getByRole("button", { name: "Refresh choices", exact: true }).click();
      await dialog.getByRole("alert").waitFor();
      assert(await submit.isDisabled());
      state.selectionFailure = false; state.selections = { available: true, options: [selection] };
      await dialog.getByRole("button", { name: "Retry choices", exact: true }).click();
      await choose(dialog);
      await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
    });
    await check(`${name}: read-only role disables management without mutations`, async () => {
      state.role = "viewer";
      const before = state.calls.length;
      await page.reload();
      await page.getByRole("link", { name: "api-sandbox", exact: true }).click();
      await card("alpha-primary").waitFor();
      assert(await page.getByRole("button", { name: "Start compute", exact: true }).isDisabled());
      assert.equal(await page.getByRole("button", { name: "Stop all VMs", exact: true }).count(), 0);
      assert.equal(state.calls.slice(before).filter(call => call.method !== "GET").length, 0);
    });
    await check(`${name}: late detail response cannot replace a different route`, async () => {
      await page.getByRole("link", { name: "Workspaces", exact: true }).last().click();
      let release;
      state.delayedDetail = { id: "beta", requested: false, promise: new Promise(resolve => { release = resolve; }) };
      await page.getByRole("link", { name: "docs-preview", exact: true }).click();
      await until(() => state.delayedDetail.requested, "delayed route requested");
      await page.goBack();
      await page.getByRole("link", { name: "api-sandbox", exact: true }).click();
      await card("alpha-primary").waitFor();
      release();
      await sleep(150);
      assert.equal(await card("beta-primary").count(), 0);
      assert.equal(await page.getByRole("heading", { name: "api-sandbox", exact: true }).count(), 1);
    });
    await check(`${name}: no external traffic, unknown APIs or page-level horizontal overflow`, async () => {
      assert.deepEqual(state.unexpected, []);
      assert.deepEqual(state.external, []);
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), "Page overflows viewport");
    });
    await check(`${name}: empty collection, failed refresh and retry remain read-only`, async () => {
      const before = mutations(state).length;
      const originalItems = state.workspaces;
      state.workspaces = [];
      await page.goto(`${origin}/workspaces`);
      await page.getByText(/No workspaces yet/).waitFor();
      assert.equal(await page.getByRole("button", { name: "Create workspace", exact: true }).count(), 0);
      state.readFailure = true;
      await page.getByRole("button", { name: "Refresh workspaces", exact: true }).click();
      await page.getByRole("alert").filter({ hasText: "Synthetic refresh failed" }).waitFor();
      state.workspaces = originalItems; state.readFailure = false;
      await page.getByRole("button", { name: "Retry", exact: true }).click();
      await page.getByRole("link", { name: "api-sandbox", exact: true }).waitFor();
      assert.equal(mutations(state).length, before);
    });
  } finally { await context.close(); }
}
try {
  const playwright = process.env.PLAYWRIGHT_MODULE
    ? await import(pathToFileURL(resolve(process.env.PLAYWRIGHT_MODULE)).href)
    : await import("playwright");
  await new Promise((resolve, reject) => {
    const probe = createServer();
    probe.once("error", reject);
    probe.listen(port, "127.0.0.1", () => probe.close(resolve));
  });
  const dashboard = join(root, "modules/dashboard");
  process.chdir(dashboard);
  const requireDashboard = createRequire(join(dashboard, "package.json"));
  const { createServer: createViteServer } = await import(pathToFileURL(requireDashboard.resolve("vite")).href);
  const { default: react } = await import(pathToFileURL(requireDashboard.resolve("@vitejs/plugin-react")).href);
  server = await createViteServer({
    configFile: false, root: dashboard, plugins: [react()],
    resolve: { alias: { "@": join(dashboard, "app") } },
    cacheDir: join(scratch, "vite-cache"),
    server: { host: "127.0.0.1", port, strictPort: true },
  });
  await server.listen();
  assert((await fetch(origin)).ok, "Vite readiness failed");
  browser = await playwright.chromium.launch({
    headless: true, executablePath: process.env.BROWSER_EXECUTABLE || undefined,
    downloadsPath: scratch, tracesDir: scratch,
  });
  report.environment.browser = browser.version();
  await runViewport("desktop", { width: 1440, height: 1000 });
  await runViewport("narrow", { width: 390, height: 844 });
  assert.deepEqual(report.errors, []);
  report.passed = true;
} catch (error) {
  report.passed = false;
  report.errors.push(error.stack ?? String(error));
  console.error(error.message);
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  if (server) await server.close();
  await rm(scratch, { recursive: true, force: true });
  report.completedAt = new Date().toISOString();
  await writeFile(join(artifacts, "report.json"), JSON.stringify(report, null, 2));
  await writeFile(join(artifacts, "requests.json"), JSON.stringify(requests, null, 2));
}
