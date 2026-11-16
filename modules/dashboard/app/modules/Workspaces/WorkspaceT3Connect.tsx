import { useState } from "react";
import { Button } from "@/components/ui/button";
import { getWorkspaceLaunchUrl } from "@/api/workspacesApi";

export function WorkspaceT3Connect({ workspaceId, vmId, organizationId, generation, ready }: {
  workspaceId: string; vmId: string; organizationId: string; generation: number; ready: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return <section aria-label="T3 Connect account linking">
    <h4>T3 Connect (optional)</h4>
    <p>Link this existing primary VM to your own T3 account. Your other signed-in devices can resume
      the same projects and threads while compute is running. This never starts or provisions compute.</p>
    <p>Linked devices have independent shell/provider access through T3, bypassing Minicloud session controls.
      Only the workspace creator or organization owner can open linking management.
      Approval, link status and unlink are handled on a private page, never in recorded T3 terminal output.
      Activation explicitly restarts the existing managed T3 service and interrupts turns and terminals.</p>
    <Button variant="outline" disabled={!ready || busy} onClick={() => {
      const popup = window.open("about:blank", "_blank");
      if (popup) popup.opener = null;
      setBusy(true); setError(null);
      void getWorkspaceLaunchUrl(workspaceId, vmId, organizationId, generation, "connect").then(result => {
        const url = new URL(result.url);
        if (url.protocol !== "https:" || !popup) throw new Error("Allow a new window to open the secure linking page.");
        popup.location.replace(url.href);
      }).catch(() => {
        popup?.close();
        setError("Link management requires a running, qualified primary VM and workspace-owner access. No compute was started.");
      }).finally(() => setBusy(false));
    }}>Manage T3 Connect</Button>
    {error && <p role="alert">{error}</p>}
  </section>;
}
