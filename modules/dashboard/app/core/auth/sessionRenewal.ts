import type { AccountSession } from "../../api/accountApi";
import { ApiError } from "../../models/controlPlane";

type Deadlines = Pick<AccountSession, "expiresAt" | "idleExpiresAt" | "renewAfter">;
type SessionMessage = { type: "renewed"; session: Deadlines } | { type: "signed-out" };

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
};

const activityEvents = ["pointerdown", "keydown", "wheel", "touchstart"] as const;
const resyncEvents = ["focus", "online", "pageshow"] as const;
const minimumRenewalInterval = 60_000;
const resyncInterval = 30_000;
const maximumTimerDelay = 6 * 60 * 60_000;
const retryDelays = [5_000, 15_000, 30_000, 60_000];

export const sessionChannelName = "minicloud-session";

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
 * polling never counts as activity. Tabs share renewals and sign-outs over a BroadcastChannel, and each tab
 * re-reads the server session before treating an idle deadline as a sign-out.
 */
export function startSessionRenewal(initial: AccountSession, options: SessionRenewalOptions) {
  const now = options.now ?? Date.now;
  const target = options.target ?? window;
  const visibilityTarget = options.visibilityTarget ?? document;
  const visibility = options.visibility ?? (() => document.visibilityState);
  const channel = options.channel === undefined ? openChannel() : options.channel;
  let deadlines: Deadlines = initial;
  let activityPending = false;
  let lastRenewal = Number.NEGATIVE_INFINITY;
  let lastRead = Number.NEGATIVE_INFINITY;
  let inFlight: Promise<void> | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let failures = 0;
  let stopped = false;

  const time = (value: string | null | undefined) => value ? Date.parse(value) : Number.NaN;
  const backoff = () => retryDelays[Math.min(failures, retryDelays.length) - 1];
  const renewAt = () => lastRenewal + (failures > 0 ? backoff() : minimumRenewalInterval);
  const readAt = () => failures > 0 ? lastRead + backoff() : Number.NEGATIVE_INFINITY;

  function apply(next: Deadlines) {
    deadlines = { expiresAt: next.expiresAt, idleExpiresAt: next.idleExpiresAt, renewAfter: next.renewAfter ?? null };
  }

  function run(work: () => Promise<AccountSession>, renewal: boolean) {
    if (inFlight) return inFlight;
    if (renewal) lastRenewal = now();
    else lastRead = now();
    inFlight = work().then(session => {
      if (stopped) return;
      failures = 0;
      apply(session);
      if (renewal) {
        activityPending = false;
        channel?.postMessage({ type: "renewed", session: deadlines } satisfies SessionMessage);
      }
      options.onSession(session);
    }, error => {
      if (stopped) return;
      if (error instanceof ApiError && error.status === 401) {
        stop();
        options.onSignedOut();
        return;
      }
      // Outages and transient failures are not a sign-out; retry with backoff instead.
      failures++;
    }).finally(() => {
      inFlight = null;
      schedule();
    });
    return inFlight;
  }

  function renewalDue() {
    return activityPending && now() >= time(deadlines.renewAfter) && now() >= renewAt();
  }

  function evaluate() {
    if (stopped || inFlight) return;
    const current = now();
    if (current >= time(deadlines.idleExpiresAt)) {
      // Another tab may have renewed; only the server decides whether the session ended.
      if (current >= readAt()) void run(options.read, false);
      else schedule();
    } else if (renewalDue()) {
      void run(options.renew, true);
    } else {
      schedule();
    }
  }

  function schedule() {
    if (stopped) return;
    clearTimeout(timer);
    const current = now();
    const candidates = [time(deadlines.idleExpiresAt)];
    if (activityPending) candidates.push(Math.max(time(deadlines.renewAfter), renewAt()));
    if (failures > 0) candidates.push(readAt());
    const future = candidates.filter(value => Number.isFinite(value) && value > current);
    const next = future.length > 0 ? Math.min(...future) : current + maximumTimerDelay;
    timer = setTimeout(evaluate, Math.min(Math.max(next - current, 1_000), maximumTimerDelay));
  }

  function activity() {
    if (stopped) return;
    const wasPending = activityPending;
    activityPending = true;
    if (renewalDue()) evaluate();
    else if (!wasPending && !inFlight) schedule();
  }

  function resync() {
    if (stopped || visibility() === "hidden") return;
    // Returning to the tab is user activity; re-read first (throttled) so changes made elsewhere are respected.
    if (now() - lastRead < resyncInterval) activity();
    else void run(options.read, false).then(activity);
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
        // Verify with the server rather than trusting another tab.
        void run(options.read, false);
      }
    };
  }

  for (const event of activityEvents) target.addEventListener(event, activity, { passive: true, capture: true });
  for (const event of resyncEvents) target.addEventListener(event, resync);
  visibilityTarget.addEventListener("visibilitychange", visibilityChanged);
  schedule();

  function stop() {
    if (stopped) return;
    stopped = true;
    clearTimeout(timer);
    for (const event of activityEvents) target.removeEventListener(event, activity, { capture: true });
    for (const event of resyncEvents) target.removeEventListener(event, resync);
    visibilityTarget.removeEventListener("visibilitychange", visibilityChanged);
    if (channel) {
      channel.onmessage = null;
      channel.close();
    }
  }

  return { stop };
}
