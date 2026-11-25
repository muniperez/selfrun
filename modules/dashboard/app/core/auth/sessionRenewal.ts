import type { AccountSession } from "../../api/accountApi";
import { ApiError } from "../../models/controlPlane";

type Deadlines = Pick<AccountSession, "expiresAt" | "idleExpiresAt" | "renewAfter">;
type SessionMessage = { type: "renewed"; session: Deadlines } | { type: "signed-out" };
/** The subset of the Web Locks API used to let one tab renew at a time. */
export type RenewalLocks = {
  request<T>(name: string, options: { ifAvailable: true }, callback: (lock: unknown) => Promise<T>): Promise<T>;
};

export type SessionRenewalOptions = {
  /** Explicit, server-validated renewal. */
  renew: () => Promise<AccountSession>;
  /** Reads current server state without extending it. */
  read: () => Promise<AccountSession>;
  onSession: (session: AccountSession) => void;
  onSignedOut: () => void;
  now?: () => number;
  target?: Pick<Window, "addEventListener" | "removeEventListener">;
  visibilityTarget?: Pick<Document, "addEventListener" | "removeEventListener">;
  visibility?: () => DocumentVisibilityState;
  channel?: Pick<BroadcastChannel, "postMessage" | "close"> & { onmessage: ((event: MessageEvent) => void) | null } | null;
  locks?: RenewalLocks | null;
};

const activityEvents = ["pointerdown", "keydown", "wheel", "touchstart"] as const;
const resyncEvents = ["focus", "pageshow"] as const;
const minimumRenewalInterval = 60_000;
const resyncInterval = 30_000;
const lockRetryDelay = 5_000;
const maximumTimerDelay = 6 * 60 * 60_000;
const retryDelays = [5_000, 15_000, 30_000, 60_000];

export const sessionChannelName = "selfrun-session";
export const renewalLockName = "selfrun-session-renewal";

function defaultLocks(): RenewalLocks | null {
  return typeof navigator !== "undefined" && navigator.locks ? navigator.locks as unknown as RenewalLocks : null;
}

function openChannel() {
  try {
    return typeof BroadcastChannel === "undefined" ? null : new BroadcastChannel(sessionChannelName);
  } catch {
    return null;
  }
}

export function broadcastSignedOut() {
  const channel = openChannel();
  channel?.postMessage({ type: "signed-out" } satisfies SessionMessage);
  channel?.close();
}

/**
 * Renews the dashboard session only after real user activity, inside the server's renewal window. Background API
 * polling and network reconnects never count as activity. One request runs at a time per tab and a Web Lock lets
 * one tab renew at a time; tabs share renewals and sign-outs over a BroadcastChannel. A sign-out message or a
 * rejected renewal always leads to a fresh read of the server session, even when another request was in flight,
 * and only that read decides whether this tab is signed out.
 */
export function startSessionRenewal(initial: AccountSession, options: SessionRenewalOptions) {
  const now = options.now ?? Date.now;
  const target = options.target ?? window;
  const visibilityTarget = options.visibilityTarget ?? document;
  const visibility = options.visibility ?? (() => document.visibilityState);
  const channel = options.channel === undefined ? openChannel() : options.channel;
  const locks = options.locks === undefined ? defaultLocks() : options.locks;
  let deadlines: Deadlines = initial;
  let activityPending = false;
  let lastRenewal = Number.NEGATIVE_INFINITY;
  let lastRead = Number.NEGATIVE_INFINITY;
  let renewalNotBefore = Number.NEGATIVE_INFINITY;
  let busy = false;
  let readQueued = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let failures = 0;
  let stopped = false;

  const time = (value: string | null | undefined) => value ? Date.parse(value) : Number.NaN;
  const backoff = () => retryDelays[Math.min(failures, retryDelays.length) - 1];
  const renewAt = () => Math.max(lastRenewal + (failures > 0 ? backoff() : minimumRenewalInterval), renewalNotBefore);
  const readAt = () => lastRead + (failures > 0 ? backoff() : resyncInterval);

  function apply(next: Deadlines) {
    deadlines = { expiresAt: next.expiresAt, idleExpiresAt: next.idleExpiresAt, renewAfter: next.renewAfter ?? null };
  }

  function renewOnce() {
    if (!locks) return options.renew();
    // Another tab holding the lock is already renewing this session; its broadcast will settle this tab.
    return locks.request(renewalLockName, { ifAvailable: true }, async lock => lock ? await options.renew() : null);
  }

  /** Runs one request at a time. A read requested while busy is queued, never folded into the running request. */
  function request(kind: "read" | "renew") {
    if (stopped) return;
    if (busy) {
      if (kind === "read") readQueued = true;
      return;
    }
    busy = true;
    clearTimeout(timer);
    const renewal = kind === "renew";
    const previousRenewal = lastRenewal;
    if (renewal) lastRenewal = now();
    else {
      lastRead = now();
      readQueued = false;
    }
    let work: Promise<AccountSession | null>;
    try {
      work = renewal ? renewOnce() : options.read();
    } catch (error) {
      work = Promise.reject(error);
    }
    void work.then(session => {
      if (stopped) return;
      if (!session) {
        lastRenewal = previousRenewal;
        renewalNotBefore = now() + lockRetryDelay;
        return;
      }
      failures = 0;
      apply(session);
      if (renewal) {
        activityPending = false;
        channel?.postMessage({ type: "renewed", session: deadlines } satisfies SessionMessage);
      }
      options.onSession(session);
    }, error => {
      if (stopped) return;
      if (error instanceof ApiError && (error.status === 401 || renewal && error.status === 403)) {
        if (renewal) {
          // The session may have been rotated or ended elsewhere; only a read of the current session decides.
          readQueued = true;
          return;
        }
        stop();
        options.onSignedOut();
        return;
      }
      // Outages, conflicts and transient failures are not a sign-out; retry with backoff instead.
      failures++;
    }).finally(() => {
      busy = false;
      if (stopped) return;
      if (readQueued) request("read");
      else evaluate();
    });
  }

  function renewalDue() {
    return activityPending && now() >= time(deadlines.renewAfter) && now() >= renewAt();
  }

  function evaluate() {
    if (stopped || busy) return;
    const current = now();
    if (current >= time(deadlines.idleExpiresAt)) {
      // Another tab may have renewed; only the server decides whether the session ended.
      if (current >= readAt()) request("read");
      else schedule();
    } else if (renewalDue()) {
      request("renew");
    } else {
      schedule();
    }
  }

  function schedule() {
    if (stopped || busy) return;
    clearTimeout(timer);
    const current = now();
    const idle = time(deadlines.idleExpiresAt);
    const candidates = [current >= idle ? readAt() : idle];
    if (activityPending) candidates.push(Math.max(time(deadlines.renewAfter), renewAt()));
    const finite = candidates.filter(value => Number.isFinite(value));
    const next = finite.length > 0 ? Math.min(...finite) : current + maximumTimerDelay;
    timer = setTimeout(evaluate, Math.min(Math.max(next - current, 1_000), maximumTimerDelay));
  }

  function activity() {
    if (stopped) return;
    const wasPending = activityPending;
    activityPending = true;
    if (renewalDue()) evaluate();
    else if (!wasPending) schedule();
  }

  function resync() {
    if (stopped || visibility() === "hidden") return;
    // Returning to the tab is user activity; re-read first (throttled) so changes made elsewhere are respected.
    if (now() - lastRead < resyncInterval) activity();
    else {
      activityPending = true;
      request("read");
    }
  }

  function reconnected() {
    // Re-check authentication without recording activity: an unattended tab must not renew because the network
    // came back.
    request("read");
  }

  function visibilityChanged() {
    if (visibility() === "visible") resync();
  }

  if (channel) {
    channel.onmessage = event => {
      const message = event.data as SessionMessage | undefined;
      if (stopped || !message || typeof message !== "object") return;
      if (message.type === "renewed" && typeof message.session?.idleExpiresAt === "string") {
        if (time(message.session.idleExpiresAt) > time(deadlines.idleExpiresAt)) {
          apply(message.session);
          activityPending = false;
          schedule();
        }
      } else if (message.type === "signed-out") {
        // Verify with the server rather than trusting another tab; queued behind any request in flight.
        request("read");
      }
    };
  }

  for (const event of activityEvents) target.addEventListener(event, activity, { passive: true, capture: true });
  for (const event of resyncEvents) target.addEventListener(event, resync);
  target.addEventListener("online", reconnected);
  visibilityTarget.addEventListener("visibilitychange", visibilityChanged);
  schedule();

  function stop() {
    if (stopped) return;
    stopped = true;
    clearTimeout(timer);
    for (const event of activityEvents) target.removeEventListener(event, activity, { capture: true });
    for (const event of resyncEvents) target.removeEventListener(event, resync);
    target.removeEventListener("online", reconnected);
    visibilityTarget.removeEventListener("visibilitychange", visibilityChanged);
    if (channel) {
      channel.onmessage = null;
      channel.close();
    }
  }

  return { stop };
}
