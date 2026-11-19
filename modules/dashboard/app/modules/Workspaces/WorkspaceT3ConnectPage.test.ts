import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { runInNewContext } from "node:vm";
import { getByRole, queryByRole } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";

const source = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.js"), "utf8");
const html = readFileSync(resolve(process.cwd(), "../../templates/development-workspaces/t3-connect.html"), "utf8");
const prompt = { url: "https://example.invalid/approve?code=SYNTHETIC-CODE", code: "SYNTHETIC-CODE" };
const idle = { operation: "idle", linked: false, desired: false };
const approved = { operation: "approval_saved_activate_required", linked: false, desired: true };
const linked = { operation: "idle", linked: true, desired: true };
const element = (id: string) => document.getElementById(id)!;
const button = (name: string) => getByRole<HTMLButtonElement>(document.body, "button", { name });
const status = () => getByRole(document.body, "status");
afterEach(() => { document.body.innerHTML = ""; });

function page(values: (object | Promise<object> | Error)[]) {
  document.body.innerHTML = html;
  const fetch = vi.fn().mockImplementation(async () => {
    const value = await values.shift();
    if (value instanceof Error) throw value;
    if (!value) throw new Error("Unexpected request");
    return { ok: true, json: async () => value };
  });
  const confirm = vi.fn(() => true);
  let interval: () => void = () => {};
  let now = 0;
  runInNewContext(source, {
    document, URL, fetch, confirm, AbortSignal,
    Date: { now: () => now },
    setInterval: (callback: () => void) => { interval = callback; },
  });
  return { fetch, confirm, poll: () => interval(), advance: (ms: number) => { now += ms; } };
}

async function settled() {
  await vi.waitFor(() => expect(element("primary")).not.toHaveAttribute("aria-disabled"));
}

it("discloses independent access and does not poll or mutate a passive page", async () => {
  const state = page([idle]);
  await settled();
  expect(status()).toHaveTextContent("Not linked");
  expect(button("Link my T3 account")).toBeVisible();
  expect(element("access")).toHaveTextContent("shell and provider-management access outside Minicloud's session controls");
  expect(element("access")).toHaveTextContent("Removing Minicloud access does not revoke the T3 link.");
  expect(element("access-details").querySelector("summary")).toBeVisible();
  expect(element("management")).toHaveAttribute("hidden");
  state.poll();
  state.confirm.mockReturnValueOnce(false);
  button("Link my T3 account").click();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  expect(state.fetch).toHaveBeenCalledWith("/status", expect.objectContaining({ method: "GET" }));
});

it("shows the private approval only while pending and requires a fresh opt-in after expiry", async () => {
  const state = page([
    idle,
    { operation: "awaiting_approval" },
    { operation: "awaiting_approval", approval: prompt },
    { operation: "approval_expired", approval: prompt },
    { operation: "awaiting_approval" },
  ]);
  await settled();
  button("Link my T3 account").click();
  await settled();
  expect(status()).toHaveTextContent("Preparing account approval");
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Waiting for account approval");
  expect(status()).not.toHaveTextContent(prompt.code);
  expect(element("code")).toHaveTextContent(prompt.code);
  const approvalLink = getByRole(document.body, "link", { name: "Open T3 account approval" });
  expect(approvalLink).toHaveAttribute("href", prompt.url);
  expect(approvalLink).toHaveAttribute("rel", "noopener noreferrer");
  approvalLink.focus();
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Approval expired");
  expect(element("code")).toBeEmptyDOMElement();
  expect(approvalLink).not.toHaveAttribute("href");
  expect(button("Start approval again")).toHaveFocus();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(4);
  button("Start approval again").click();
  await settled();
  expect(state.fetch).toHaveBeenNthCalledWith(5, "/link", expect.objectContaining({
    method: "POST", headers: { "X-Minicloud-Connect": "1" }, cache: "no-store",
    credentials: "same-origin", redirect: "error",
  }));
});

it("confirms activation, prevents duplicate requests, and reconnects after transport loss", async () => {
  let loseConnection = () => {};
  const activation = new Promise<object>((_, reject) => {
    loseConnection = () => reject(new Error("Restart closed the connection"));
  });
  const state = page([
    { operation: "awaiting_approval", approval: prompt }, approved, activation,
    new Error("Management temporarily unavailable"), { ...linked, reachable: false }, { ...linked, reachable: true },
  ]);
  await settled();
  state.poll();
  await settled();
  expect(element("code")).toBeEmptyDOMElement();
  expect(status()).toHaveTextContent("Account approved");
  expect(getByRole(document.body, "list", { name: "Connection setup" }).querySelector('[aria-current="step"]'))
    .toHaveTextContent("Activate link");
  expect(element("message")).toHaveTextContent("Finish active turns and save your work before restarting T3.");
  expect(element("message")).toHaveTextContent("T3 terminals will close. The VM stays running");
  const activate = button("Activate and restart T3");
  state.confirm.mockReturnValueOnce(false);
  activate.click();
  expect(state.fetch).toHaveBeenCalledTimes(2);
  activate.click();
  activate.click();
  state.poll();
  expect(status()).toHaveTextContent("Restarting T3");
  expect(state.fetch).toHaveBeenCalledTimes(3);
  loseConnection();
  await settled();
  for (let attempt = 0; attempt < 2; attempt++) {
    state.poll();
    await settled();
    expect(status()).toHaveTextContent("Restarting T3");
  }
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Ready to connect");
  expect(getByRole(document.body, "link", { name: "Open T3 web app" })).toBeVisible();
  expect(element("unlink")).not.toBeVisible();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(6);
});

it("bounds restart polling and asks for a status check rather than retrying activation", async () => {
  const state = page([approved, { operation: "restart_requested_check_status" }, new Error("Offline")]);
  await settled();
  button("Activate and restart T3").click();
  await settled();
  state.advance(120_000);
  state.poll();
  await settled();
  expect(status()).toHaveTextContent("Restart not confirmed");
  expect(button("Check link status")).toBeVisible();
  state.poll();
  expect(state.fetch).toHaveBeenCalledTimes(3);
});

it("distinguishes unreachable, revoked, and failed states without rendering raw data", async () => {
  const state = page([
    { ...linked, reachable: false, vmId: "private-vm-id", environmentId: "private-environment" },
    { ...linked, revoked: true, reachable: true },
    { operation: "approval_failed", diagnostic: "private-cli-error" },
  ]);
  await settled();
  expect(status()).toHaveTextContent("Linked but not reachable");
  expect(element("troubleshooting").querySelector("summary")).toBeVisible();
  expect(queryByRole(document.body, "link", { name: "Open T3 web app" })).toBeNull();
  expect(document.body).not.toHaveTextContent(/private-vm-id|private-environment/);
  button("Check link status").click();
  await settled();
  expect(status()).toHaveTextContent("Link revoked");
  button("Link my T3 account").click();
  await settled();
  expect(status()).toHaveTextContent("Approval did not finish");
  expect(document.body).not.toHaveTextContent("private-cli-error");
});

it.each([
  [{ operation: "unlinked", remoteRemovalConfirmed: false }, "Unlinked here; remote removal unconfirmed"],
  [{ operation: "unlink_not_confirmed", remoteRemovalConfirmed: false }, "Unlink not confirmed"],
  [new Error("Unlink response lost"), "Unlink not confirmed"],
])("confirms unlink and explains account-side removal if the result is incomplete (%j)", async (result, heading) => {
  const state = page([{ ...linked, reachable: true }, result]);
  await settled();
  element("management").querySelector("summary")!.click();
  state.confirm.mockReturnValueOnce(false);
  button("Unlink T3 Connect").click();
  expect(state.fetch).toHaveBeenCalledTimes(1);
  button("Unlink T3 Connect").click();
  await settled();
  expect(state.fetch).toHaveBeenNthCalledWith(2, "/unlink", expect.objectContaining({ method: "POST" }));
  expect(status()).toHaveTextContent(heading);
  expect(element("message")).toHaveTextContent("sign in to your T3 account and remove this machine from T3 Connect");
});
