import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AccountSession } from "../../api/accountApi";
import { ApiError } from "../../models/controlPlane";
import { renewalLockName, startSessionRenewal, type RenewalLocks } from "./sessionRenewal";

const start = Date.parse("2026-01-01T00:00:00Z");
const at = (minutes: number) => new Date(start + minutes * 60_000).toISOString();
const session = (idle: number, renewAfter: number | null, csrfToken = "csrf"): AccountSession => ({
  userId: "user-1", csrfToken, expiresAt: at(720), idleExpiresAt: at(idle), renewAfter: renewAfter === null ? null : at(renewAfter)
});
const failure = (status: number) => new ApiError(status, { code: status === 401 ? "unauthenticated" : "auth_unavailable", message: "", details: [] });

function channel() {
  return { postMessage: vi.fn(), close: vi.fn(), onmessage: null as ((event: MessageEvent) => void) | null };
}

/** Channels for two tabs that deliver each other's messages, like a BroadcastChannel. */
function linkedChannels() {
  const first = channel();
  const second = channel();
  first.postMessage.mockImplementation(data => second.onmessage?.({ data } as MessageEvent));
  second.postMessage.mockImplementation(data => first.onmessage?.({ data } as MessageEvent));
  return [first, second] as const;
}

/** An in-memory Web Locks manager shared by tabs, honoring `ifAvailable`. */
function sharedLocks() {
  const requests: string[] = [];
  let held = false;
  const locks: RenewalLocks = {
    async request(name, _options, callback) {
      requests.push(name);
      if (held) return callback(null);
      held = true;
      try {
        return await callback({ name });
      } finally {
        held = false;
      }
    }
  };
  return { locks, requests };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail; });
  return { promise, resolve, reject };
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
    locks: null,
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

  it("reads the session after an in-flight renewal when another tab signs out meanwhile", async () => {
    const pending = deferred<AccountSession>();
    const renew = vi.fn(() => pending.promise);
    const read = vi.fn().mockRejectedValue(failure(401));
    const { options, act } = setup({ renew, read });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    expect(renew).toHaveBeenCalledOnce();

    options.channel.onmessage!({ data: { type: "signed-out" } } as MessageEvent);
    options.channel.onmessage!({ data: { type: "signed-out" } } as MessageEvent);
    await vi.advanceTimersByTimeAsync(0);
    expect(read).not.toHaveBeenCalled();

    // The renewal committed before the sign-out, so it succeeds; the queued read still runs and wins.
    pending.resolve(session(150, 90));
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledOnce();
    expect(options.onSignedOut).toHaveBeenCalledOnce();
    expect(options.channel.close).toHaveBeenCalled();
  });

  it("confirms a rejected renewal with a read instead of signing out from it", async () => {
    const rotated = session(150, 90, "rotated-csrf");
    const renew = vi.fn().mockRejectedValueOnce(failure(401)).mockRejectedValueOnce(failure(403));
    const read = vi.fn().mockResolvedValueOnce(rotated).mockResolvedValueOnce(rotated).mockRejectedValueOnce(failure(401));
    const { options, act } = setup({ renew, read });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    await vi.advanceTimersByTimeAsync(0);
    expect(renew).toHaveBeenCalledOnce();
    expect(read).toHaveBeenCalledOnce();
    expect(options.onSession).toHaveBeenLastCalledWith(rotated);
    expect(options.onSignedOut).not.toHaveBeenCalled();

    vi.setSystemTime(start + 91 * 60_000);
    act();
    await vi.advanceTimersByTimeAsync(0);
    expect(renew).toHaveBeenCalledTimes(2);
    expect(read).toHaveBeenCalledTimes(2);
    expect(options.onSignedOut).not.toHaveBeenCalled();

    options.channel.onmessage!({ data: { type: "signed-out" } } as MessageEvent);
    await vi.advanceTimersByTimeAsync(0);
    expect(options.onSignedOut).toHaveBeenCalledOnce();
  });

  it("re-checks authentication on reconnect without counting it as activity", async () => {
    const read = vi.fn(async () => session(120, 60));
    const { options, target, act } = setup({ read, visibility: () => "visible" });
    vi.setSystemTime(start + 61 * 60_000);
    target.dispatchEvent(new Event("online"));
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledOnce();
    expect(options.onSession).toHaveBeenCalledWith(session(120, 60));

    await vi.advanceTimersByTimeAsync(58 * 60_000);
    expect(options.renew).not.toHaveBeenCalled();
    expect(read).toHaveBeenCalledOnce();

    act();
    await vi.advanceTimersByTimeAsync(0);
    expect(options.renew).toHaveBeenCalledOnce();
  });

  it("queues a reconnect check behind an in-flight renewal", async () => {
    const pending = deferred<AccountSession>();
    const read = vi.fn(async () => session(150, 90));
    const { options, target, act } = setup({ renew: vi.fn(() => pending.promise), read });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    target.dispatchEvent(new Event("online"));
    await vi.advanceTimersByTimeAsync(0);
    expect(read).not.toHaveBeenCalled();
    pending.reject(new TypeError("network"));
    await vi.advanceTimersByTimeAsync(0);
    expect(read).toHaveBeenCalledOnce();
    expect(options.renew).toHaveBeenCalledOnce();
  });

  it("lets one tab renew at a time and settles the other from its broadcast", async () => {
    const { locks, requests } = sharedLocks();
    const [firstChannel, secondChannel] = linkedChannels();
    const pending = deferred<AccountSession>();
    const first = setup({ renew: vi.fn(() => pending.promise), channel: firstChannel, locks });
    const second = setup({ channel: secondChannel, locks });
    vi.setSystemTime(start + 61 * 60_000);
    first.act();
    await vi.advanceTimersByTimeAsync(0);
    second.act();
    await vi.advanceTimersByTimeAsync(0);
    expect(requests).toEqual([renewalLockName, renewalLockName]);
    expect(first.options.renew).toHaveBeenCalledOnce();
    expect(second.options.renew).not.toHaveBeenCalled();

    pending.resolve(session(150, 90));
    await vi.advanceTimersByTimeAsync(0);
    expect(second.options.onSession).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(20 * 60_000);
    expect(second.options.renew).not.toHaveBeenCalled();
    expect(requests).toHaveLength(2);
    first.renewal.stop();
    second.renewal.stop();
  });

  it("retries shortly when another tab held the renewal lock but did not renew", async () => {
    const { locks } = sharedLocks();
    const blocker = deferred<void>();
    void locks.request(renewalLockName, { ifAvailable: true }, () => blocker.promise);
    const { options, act } = setup({ locks });
    vi.setSystemTime(start + 61 * 60_000);
    act();
    await vi.advanceTimersByTimeAsync(0);
    expect(options.renew).not.toHaveBeenCalled();
    blocker.resolve();
    await vi.advanceTimersByTimeAsync(5_000);
    expect(options.renew).toHaveBeenCalledOnce();
    expect(options.onSession).toHaveBeenCalledWith(session(150, 90));
  });
});
