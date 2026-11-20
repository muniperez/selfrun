import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AccountSession } from "../../api/accountApi";
import { ApiError } from "../../models/controlPlane";
import { startSessionRenewal } from "./sessionRenewal";

const start = Date.parse("2026-01-01T00:00:00Z");
const at = (minutes: number) => new Date(start + minutes * 60_000).toISOString();
const session = (idle: number, renewAfter: number | null, csrfToken = "csrf"): AccountSession => ({
  userId: "user-1", csrfToken, expiresAt: at(720), idleExpiresAt: at(idle), renewAfter: renewAfter === null ? null : at(renewAfter)
});
const failure = (status: number) => new ApiError(status, { code: status === 401 ? "unauthenticated" : "auth_unavailable", message: "", details: [] });

function channel() {
  return { postMessage: vi.fn(), close: vi.fn(), onmessage: null as ((event: MessageEvent) => void) | null };
}

function setup(overrides: Partial<Parameters<typeof startSessionRenewal>[1]> = {}, initial = session(120, 60)) {
  const target = new EventTarget();
  const visibilityTarget = new EventTarget();
  const options = {
    renew: vi.fn(async () => session(150, 90)),
    read: vi.fn(async () => initial),
    onSession: vi.fn(),
    onSignedOut: vi.fn(),
    target: target as unknown as Window,
    visibilityTarget: visibilityTarget as unknown as Document,
    visibility: () => "visible" as DocumentVisibilityState,
    channel: channel(),
    ...overrides
  };
  const renewal = startSessionRenewal(initial, options);
  return { options: { ...options, channel: options.channel! }, renewal, act: () => target.dispatchEvent(new Event("pointerdown")), target };
}


beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(start); });
afterEach(() => { vi.useRealTimers(); });

describe("session renewal", () => {
  it("renews once per window only after user activity, never from background time alone", async () => {
    const { options, act } = setup();
    await vi.advanceTimersByTimeAsync(70 * 60_000);
    expect(options.renew).not.toHaveBeenCalled();

    act(); act(); act();
    await vi.advanceTimersByTimeAsync(0);
    expect(options.renew).toHaveBeenCalledOnce();
    expect(options.onSession).toHaveBeenCalledWith(session(150, 90));
    expect(options.channel.postMessage).toHaveBeenCalledWith({ type: "renewed", session: { expiresAt: at(720), idleExpiresAt: at(150), renewAfter: at(90) } });

    act();
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(options.renew).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(11 * 60_000);
    expect(options.renew).toHaveBeenCalledTimes(2);
  });

  it("activity before the renewal window renews when the window opens", async () => {
    const { options, act } = setup();
    act();
    await vi.advanceTimersByTimeAsync(59 * 60_000);
    expect(options.renew).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(options.renew).toHaveBeenCalledOnce();
  });

  it("confirms an idle deadline with the server and signs out only on 401", async () => {
    const read = vi.fn().mockRejectedValueOnce(failure(503)).mockRejectedValueOnce(failure(401));
    const { options } = setup({ read });
    await vi.advanceTimersByTimeAsync(120 * 60_000);
    expect(read).toHaveBeenCalledOnce();
    expect(options.onSignedOut).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(read).toHaveBeenCalledTimes(2);
    expect(options.onSignedOut).toHaveBeenCalledOnce();
  });

  it("treats renewal outages as recoverable and retries with backoff", async () => {
    const renew = vi.fn().mockRejectedValueOnce(failure(503)).mockRejectedValueOnce(new TypeError("network"))
      .mockResolvedValueOnce(session(150, 90));
    const { options, act } = setup({ renew });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    await vi.advanceTimersByTimeAsync(0);
    expect(renew).toHaveBeenCalledOnce();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(renew).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(15_000);
    expect(renew).toHaveBeenCalledTimes(3);
    expect(options.onSession).toHaveBeenCalledWith(session(150, 90));
    expect(options.onSignedOut).not.toHaveBeenCalled();
  });

  it("ignores an in-flight renewal after it is stopped", async () => {
    let resolve!: (value: AccountSession) => void;
    const renew = vi.fn(() => new Promise<AccountSession>(done => { resolve = done; }));
    const { options, act, renewal } = setup({ renew });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    renewal.stop();
    resolve(session(180, 120));
    await vi.advanceTimersByTimeAsync(0);
    expect(options.onSession).not.toHaveBeenCalled();
    expect(options.channel.close).toHaveBeenCalled();
  });

  it("adopts renewals from other tabs and verifies their sign-outs with the server", async () => {
    const read = vi.fn().mockRejectedValue(failure(401));
    const { options } = setup({ read });
    options.channel.onmessage!({ data: { type: "renewed", session: { expiresAt: at(720), idleExpiresAt: at(200), renewAfter: at(140) } } } as MessageEvent);
    await vi.advanceTimersByTimeAsync(121 * 60_000);
    expect(read).not.toHaveBeenCalled();

    options.channel.onmessage!({ data: { type: "signed-out" } } as MessageEvent);
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledOnce();
    expect(options.onSignedOut).toHaveBeenCalledOnce();
  });

  it("re-reads the session when the tab becomes visible and renews if the window is open", async () => {
    const read = vi.fn(async () => session(120, 60));
    const { options, target } = setup({ read });
    vi.setSystemTime(start + 61 * 60_000);
    target.dispatchEvent(new Event("focus"));
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledOnce();
    expect(options.renew).toHaveBeenCalledOnce();
  });
});
