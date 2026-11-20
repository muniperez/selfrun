#!/usr/bin/env node
// Native qualification controller; never logs credentials or terminal payloads.
import fs from "node:fs";
import readline from "node:readline";
import { sourceControlEvidence } from "./source-control-probe.mjs";

const origin = "http://127.0.0.1:3773";
const terminal = { threadId: "native-qualification", terminalId: "native-terminal" };
let bearer;
let socket;
let nextId = 0;
const pending = new Map();
const timeout = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
function requireTrue(value) {
  if (!value) throw new Error("NATIVE_T3_ASSERTION_FAILED");
}
async function http(path, token, method = "GET", payload) {
  const headers = { "x-t3-orchestration-protocol": "2" };
  if (token) headers.authorization = `Bearer ${token}`;
  if (payload !== undefined) headers["content-type"] = "application/json";
  const response = await fetch(origin + path, {
    method, headers, body: payload === undefined ? undefined : JSON.stringify(payload),
    signal: AbortSignal.timeout(10000), redirect: "error",
  });
  const text = await response.text();
  return { status: response.status, body: text ? JSON.parse(text) : null };
}
function openSocket(ticket, protocol = true) {
  return new Promise((resolve, reject) => {
    const url = new URL("/ws", origin.replace("http:", "ws:"));
    url.searchParams.set("wsTicket", ticket);
    if (protocol) url.searchParams.set("orchestrationProtocol", "2");
    const ws = new WebSocket(url);
    const timer = setTimeout(() => { ws.close(); reject(new Error("WS_TIMEOUT")); }, 10000);
    ws.addEventListener("open", () => { clearTimeout(timer); resolve(ws); }, { once: true });
    ws.addEventListener("error", () => { clearTimeout(timer); reject(new Error("WS_FAILED")); }, { once: true });
  });
}
function onMessage(ws, message) {
  for (const value of Array.isArray(message) ? message : [message]) {
    if (value._tag === "Ping") { ws.send(JSON.stringify({ _tag: "Pong" })); continue; }
    if (value._tag === "Pong") { pending.get("pong")?.resolve(true); continue; }
    const id = String(value.requestId);
    const request = pending.get(id);
    if (!request) continue;
    if (value._tag === "Chunk") {
      ws.send(JSON.stringify({ _tag: "Ack", requestId: id }));
      if (request.stream) request.resolve(value.values);
    } else if (value._tag === "Exit") {
      pending.delete(id);
      if (value.exit?._tag === "Success") request.resolve(value.exit.value ?? null);
      else request.reject(new Error("RPC_REJECTED"));
    }
  }
}
function attach(ws) {
  ws.addEventListener("message", (event) => {
    try { onMessage(ws, JSON.parse(event.data)); } catch {}
  });
}
async function rpc(ws, tag, payload, stream = false) {
  const id = String(++nextId);
  return await new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error("RPC_TIMEOUT")); }, 10000);
    pending.set(id, {
      stream,
      resolve: (value) => { clearTimeout(timer); resolve(value); },
      reject: (error) => { clearTimeout(timer); reject(error); },
    });
    ws.send(JSON.stringify({ _tag: "Request", id, tag, payload, headers: [] }));
  });
}
async function ticket(token) {
  const result = await http("/api/auth/websocket-ticket", token, "POST");
  requireTrue(result.status === 200 && typeof result.body.ticket === "string");
  return result.body.ticket;
}
async function snapshot(projectId) {
  const result = await http("/api/orchestration/shell", bearer);
  requireTrue(result.status === 200 && Number.isSafeInteger(result.body.snapshotSequence)
    && result.body.snapshotSequence >= 0 && Array.isArray(result.body.threads)
    && Array.isArray(result.body.archivedThreads));
  const inactive = new Set(["idle", "completed", "interrupted", "failed", "cancelled", "rolled_back"]);
  for (const thread of [...result.body.threads, ...result.body.archivedThreads]) {
    requireTrue(thread.activeRunId === null && (thread.activityRunStatus ?? null) === null
      && thread.pendingRuntimeRequest === null && inactive.has(thread.status)
      && thread.hasActionableProposedPlan === false && thread.blockedOnThreadWait !== true
      && Array.isArray(thread.pendingBackgroundTasks ?? []) && (thread.pendingBackgroundTasks ?? []).length === 0);
  }
  if (projectId !== undefined) {
    requireTrue(Array.isArray(result.body.projects)
      && result.body.projects.some((project) => project.id === projectId));
  }
  return { snapshotSequence: result.body.snapshotSequence };
}
async function handle(request) {
  if (request.operation === "init") {
    for (const path of Object.values(request.tokenFiles)) {
      const st = fs.lstatSync(path);
      requireTrue(st.isFile() && st.uid === 0 && (st.mode & 0o077) === 0 && !st.isSymbolicLink());
    }
    bearer = fs.readFileSync(request.tokenFiles.operate, "utf8").trim();
    const readonly = fs.readFileSync(request.tokenFiles.readonly, "utf8").trim();
    let descriptor;
    for (let attempt = 0; attempt < 50; attempt++) {
      try { descriptor = await http("/.well-known/t3/environment"); break; } catch { await timeout(200); }
    }
    requireTrue(descriptor?.status === 200 && descriptor.body.orchestrationProtocolVersion === 2
      && typeof descriptor.body.environmentId === "string" && descriptor.body.environmentId.length > 0);
    const session = await http("/api/auth/session", bearer);
    requireTrue(session.status === 200 && session.body.authenticated === true
      && session.body.sessionMethod === "bearer-access-token"
      && Date.parse(session.body.expiresAt) > Date.now()
      && Date.parse(session.body.expiresAt) <= Date.now() + 915000);
    const permissions = session.body.permissions ?? session.body.scopes;
    requireTrue(["orchestration:read", "orchestration:operate", "terminal:read", "terminal:operate"]
      .every((scope) => permissions.includes(scope)));
    const unauthorized = await http("/api/orchestration/shell");
    requireTrue([401, 403].includes(unauthorized.status));
    const denied = await http("/api/projects/mutate", readonly, "POST", {
      type: "project.create", commandId: "native-denied", projectId: "native-denied",
      title: "native-denied", workspaceRoot: "/workspace",
    });
    requireTrue(denied.status === 403);
    const readSocket = await openSocket(await ticket(readonly));
    attach(readSocket);
    let scopeDenied = false;
    try { await rpc(readSocket, "terminal.open", { ...terminal, cwd: "/workspace" }); }
    catch (error) { scopeDenied = error.message === "RPC_REJECTED"; }
    readSocket.close();
    requireTrue(scopeDenied);
    let missingProtocolRejected = false;
    try { const wrong = await openSocket(await ticket(bearer), false); wrong.close(); }
    catch { missingProtocolRejected = true; }
    requireTrue(missingProtocolRejected);
    socket = await openSocket(await ticket(bearer));
    attach(socket);
    let afterProject = await snapshot();
    if (request.createProject === true) {
      requireTrue(typeof request.projectId === "string" && request.projectId.length > 0);
      const beforeProject = afterProject;
      const created = await http("/api/projects/mutate", bearer, "POST", {
        type: "project.create", commandId: crypto.randomUUID(), projectId: request.projectId,
        title: "Native qualification fixture", workspaceRoot: "/workspace",
      });
      requireTrue([200, 201].includes(created.status));
      for (let attempt = 0; attempt < 50; attempt++) {
        afterProject = await snapshot();
        if (afterProject.snapshotSequence > beforeProject.snapshotSequence) break;
        await timeout(100);
      }
      requireTrue(afterProject.snapshotSequence > beforeProject.snapshotSequence);
    }
    if (request.projectId !== undefined) afterProject = await snapshot(request.projectId);
    return { descriptorProtocol: 2, environmentId: descriptor.body.environmentId,
      actualSessionScopesChecked: true, unauthorizedHttpDenied: true,
      readonlyHttpMutationDenied: true, readonlyTerminalMutationDenied: true,
      protocolMissingWsDenied: true, realTicketWebSocketOpened: true,
      actualProjectMutationAdvancedApplicationCursor: request.createProject === true, ...afterProject };
  }
  if (request.operation === "snapshot") return await snapshot();
  if (request.operation === "source-control") {
    const discovery = await rpc(socket, "server.discoverSourceControl", {});
    const evidence = sourceControlEvidence(discovery, request.versions);
    await rpc(socket, "terminal.write", { ...terminal,
      data: "if gh --version >/dev/null 2>&1 && glab --version >/dev/null 2>&1; then "
        + "printf 'source-control-clis-ok\\n' > /workspace/artifacts/native-source-control-clis; fi\n",
    });
    return evidence;
  }
  if (request.operation === "scratch-env") {
    requireTrue(Array.isArray(request.names) && request.names.length > 0
      && request.names.every((name) => /^[A-Za-z_][A-Za-z0-9_]*$/.test(name)));
    // Only the named variables leave the terminal; the T3 environment may hold other values.
    await rpc(socket, "terminal.write", { ...terminal,
      data: "{ for n in " + request.names.join(" ") + "; do printf '%s=%s\\n' \"$n\" \"$(printenv \"$n\")\"; done; "
        + "touch \"$XDG_CACHE_HOME/native-terminal-probe\" \"$TMPDIR/native-terminal-probe\" && echo scratch-writable; } "
        + "> /workspace/artifacts/native-scratch-env.partial && "
        + "mv /workspace/artifacts/native-scratch-env.partial /workspace/artifacts/native-scratch-env\n",
    });
    return { terminalScratchProbeWritten: true };
  }
  if (request.operation === "open") {
    const result = await rpc(socket, "terminal.open", { ...terminal, cwd: "/workspace", cols: 100, rows: 32 });
    requireTrue(result.status === "running" && Number.isInteger(result.pid));
    return { pid: result.pid, status: result.status };
  }
  if (request.operation === "passive") {
    const chunks = await rpc(socket, "terminal.observe", terminal, true);
    requireTrue(Array.isArray(chunks) && chunks.some((chunk) => chunk.type === "snapshot"));
    await rpc(socket, "terminal.resize", { ...terminal, cols: 101, rows: 33 });
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("PING_TIMEOUT")), 5000);
      pending.set("pong", { resolve: () => { clearTimeout(timer); pending.delete("pong"); resolve(); } });
      socket.send(JSON.stringify({ _tag: "Ping" }));
    });
    await timeout(300);
    return { roundTrips: ["terminal.observe/read", "terminal.resize", "effect.Ping/Pong"] };
  }
  if (request.operation === "input") {
    const commands = {
      marker: "printf 'native-input\\n' > /workspace/artifacts/native-input-seen\n",
      foreground: "/usr/bin/sleep 4\n",
      background: "/usr/bin/sleep 4 &\n",
      scratch: "printf 'terminal-scratch\\n' > /workspace/repos/native-scratch/node_modules/.native-terminal-write\n",
    };
    requireTrue(Object.hasOwn(commands, request.kind));
    await rpc(socket, "terminal.write", { ...terminal, data: commands[request.kind] });
    return { actualTerminalWriteAcknowledged: true };
  }
  if (request.operation === "close") {
    await rpc(socket, "terminal.close", { ...terminal, deleteHistory: true });
    socket.close();
    return { ownSyntheticTerminalClosed: true };
  }
  throw new Error("UNKNOWN_PROBE_OPERATION");
}

const reader = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const line of reader) {
  let request;
  try {
    requireTrue(line.length < 65536);
    request = JSON.parse(line);
    const result = await handle(request);
    process.stdout.write(JSON.stringify({ id: request.id, ok: true, result }) + "\n");
  } catch {
    process.stdout.write(JSON.stringify({ id: request?.id ?? null, ok: false,
      failureCode: "NATIVE_T3_PROBE_FAILED_REDACTED" }) + "\n");
  }
}
socket?.close();
setTimeout(() => process.exit(0), 100).unref();
