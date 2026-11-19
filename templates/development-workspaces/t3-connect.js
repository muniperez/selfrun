const status = document.getElementById("status");
const approval = document.getElementById("approval");
const buttons = [...document.querySelectorAll("button")];
let busy = false;
let generation = 0;
let pendingApproval = false;
let activationChecks = 0;
async function request(action) {
  if (busy) return;
  if (action === "activate" && !confirm("Interrupt T3 turns and terminals, and restart only this VM's managed T3 service?")) return;
  if (action === "link" && !confirm("Grant your T3 account independent relay access to this VM? This bypasses Minicloud session controls.")) return;
  if (action === "unlink" && !confirm("Disable T3 Connect and clear the stored T3 account sign-in? Coding-provider sign-ins are not removed.")) return;
  busy = true;
  buttons.forEach(button => { button.disabled = true; });
  const current = ++generation;
  try {
    const response = await fetch(action === "refresh" ? "/status" : `/${action}`, {
      method: action === "refresh" ? "GET" : "POST",
      headers: action === "refresh" ? {} : { "X-Minicloud-Connect": "1" },
      cache: "no-store",
      credentials: "same-origin",
      redirect: "error",
    });
    const value = await response.json();
    if (current !== generation) return;
    pendingApproval = value.operation === "awaiting_approval";
    if (action === "activate" && response.ok) activationChecks = 8;
    approval.hidden = !value.approval;
    document.getElementById("code").textContent = value.approval?.code ?? "";
    const url = document.getElementById("url");
    url.removeAttribute("href");
    if (value.approval && new URL(value.approval.url).protocol === "https:") url.href = value.approval.url;
    const safe = { ...value };
    delete safe.approval;
    const summary = value.remoteRemovalConfirmed === false ? "Remote registration removal not confirmed; remove the machine in your T3 account."
      : value.revoked ? "T3 revoked this link. Explicitly link again if you want to restore access."
      : value.linked && value.reachable ? "Linked and reachable."
      : value.linked ? "Linked, but the tunnel is not confirmed reachable."
      : value.desired ? "Account approved; activate the existing managed T3 service."
      : value.operation === "unlinked" ? "Unlinked locally."
      : value.operation ?? "Not linked.";
    status.textContent = `${summary}\n${JSON.stringify(safe, null, 2)}`;
    if (!response.ok) approval.hidden = true;
  } catch {
    pendingApproval = false;
    approval.hidden = true;
    document.getElementById("code").textContent = "";
    document.getElementById("url").removeAttribute("href");
    status.textContent = "Unavailable or restarting. Refresh after the managed service is ready; this does not start compute.";
  } finally {
    busy = false;
    buttons.forEach(button => { button.disabled = false; });
  }
}
buttons.forEach(button => button.addEventListener("click", () => { void request(button.id); }));
void request("refresh");
setInterval(() => {
  if (busy || document.hidden || !pendingApproval && activationChecks === 0) return;
  if (activationChecks > 0) --activationChecks;
  void request("refresh");
}, 3000);
