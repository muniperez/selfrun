import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";
import { afterEach, expect, it, vi } from "vitest";

const source = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.js"), "utf8");
const html = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.html"), "utf8");
afterEach(() => { document.body.innerHTML = ""; });

function page(values: object[]) {
  document.body.innerHTML = html;
  const fetch = vi.fn().mockImplementation(async () => ({ ok: true, json: async () => values.shift() ?? {} }));
  let interval: () => void = () => {};
  runInNewContext(source, {
    document, URL, fetch, confirm: () => true,
    setInterval: (callback: () => void) => { interval = callback; },
  });
  return { fetch, poll: () => interval() };
}

async function settled() {
  await vi.waitFor(() => expect(document.getElementById("refresh")).not.toBeDisabled());
}

it("does not keep polling a passive management page and interfere with idle shutdown", async () => {
  const state = page([{ operation: "idle", linked: false, desired: false }]);
  await settled();
  state.poll();
  await settled();
  expect(state.fetch).toHaveBeenCalledTimes(1);
});

it("polls only a pending approval and clears its code when approval finishes", async () => {
  const state = page([
    { operation: "idle" },
    { operation: "awaiting_approval", approval: { url: "https://example.invalid/approve", code: "SYNTHETIC-CODE" } },
    { operation: "approval_saved_activate_required", linked: false, desired: true },
  ]);
  await settled();
  document.getElementById("link")!.click();
  await settled();
  expect(document.getElementById("code")).toHaveTextContent("SYNTHETIC-CODE");
  expect(document.getElementById("status")).not.toHaveTextContent("SYNTHETIC-CODE");
  state.poll();
  await settled();
  expect(document.getElementById("code")).toHaveTextContent("");
  expect(document.getElementById("approval")).not.toBeVisible();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(3);
  expect(state.fetch).toHaveBeenNthCalledWith(2, "/link", expect.objectContaining({
    method: "POST", headers: { "X-Minicloud-Connect": "1" }, cache: "no-store",
  }));
});
