import { useEffect, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { getWorkspaceLaunchUrl } from "@/api/workspacesApi";

type Props = {
  workspaceId: string; vmId: string; organizationId: string; generation: number; ready: boolean; revision?: number;
};

export function WorkspaceT3Connect(props: Props) {
  return <ScopedT3Connect key={`${props.organizationId}:${props.workspaceId}:${props.vmId}:${props.generation}`} {...props} />;
}

function ScopedT3Connect({ workspaceId, vmId, organizationId, generation, ready, revision }: Props) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pending = useRef<Window | null>(null);
  const inFlight = useRef(false);
  const mounted = useRef(true);
  const sequence = useRef(0);
  const context = JSON.stringify([ready, revision]);
  const currentContext = useRef(context);
  currentContext.current = context;
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; ++sequence.current; pending.current?.close(); };
  }, []);
  useEffect(() => {
    ++sequence.current; pending.current?.close(); pending.current = null;
    inFlight.current = false; setBusy(false); setError(null);
  }, [context]);
  return <section aria-label="T3 Connect account linking" className="space-y-2 text-sm">
    <h4 className="font-medium">T3 Connect (optional)</h4>
    <p>Link this primary VM to your own T3 account. Your other signed-in devices can resume
      the same projects and threads while the VM is running. Linking never starts a VM.</p>
    <p>Linked devices get their own shell and provider access through T3, outside Minicloud's session controls.
      Only the dev environment's creator or an organization owner can manage linking.
      Approval, link status and unlinking happen on a private page, never in recorded T3 terminal output.
      Turning linking on restarts T3 on this VM and interrupts running turns and terminals.</p>
    <Button variant="outline" disabled={!ready || busy} onClick={() => {
      if (!ready || inFlight.current) return;
      const popup = window.open("about:blank", "_blank");
      if (!popup) { setError("Allow a new window to open the secure linking page."); return; }
      popup.opener = null;
      pending.current = popup;
      const request = ++sequence.current;
      const expectedContext = context;
      inFlight.current = true;
      setBusy(true); setError(null);
      void getWorkspaceLaunchUrl(workspaceId, vmId, organizationId, generation, "connect").then(result => {
        if (!mounted.current || sequence.current !== request || currentContext.current !== expectedContext) {
          popup.close(); return;
        }
        const url = new URL(result.url);
        if (url.protocol !== "https:" || !url.hostname || url.username || url.password ||
            !Number.isFinite(Date.parse(result.expiresAt)) || Date.parse(result.expiresAt) <= Date.now())
          throw new Error("Invalid or expired management launch.");
        popup.location.replace(url.href);
        pending.current = null;
      }).catch(() => {
        popup.close();
        if (mounted.current && sequence.current === request && currentContext.current === expectedContext)
          setError("Managing T3 Connect needs a running primary VM and owner access. This didn't start the VM.");
      }).finally(() => {
        if (pending.current === popup) pending.current = null;
        if (mounted.current && sequence.current === request) { inFlight.current = false; setBusy(false); }
      });
    }}>Manage T3 Connect</Button>
    {error && <p role="alert">{error}</p>}
  </section>;
}
