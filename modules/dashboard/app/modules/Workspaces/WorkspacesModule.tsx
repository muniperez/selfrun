import { useCallback, useEffect, useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { PageScaffold } from "@/core/components/PageScaffold";
import { useAuth } from "@/core/auth/AuthProvider";
import {
  createDevelopmentWorkspace,
  listDevelopmentWorkspaces,
  mutateDevelopmentWorkspace,
} from "@/api/environmentsApi";
import type { DevelopmentWorkspace } from "@/models/controlPlane";

export function WorkspacesModule() {
  const { selectedOrganization } = useAuth();
  const [items, setItems] = useState<DevelopmentWorkspace[]>([]);
  const [name, setName] = useState("");
  const [imageId, setImageId] = useState("");
  const [profile, setProfile] = useState("Standard");
  const [planId, setPlanId] = useState("");
  const [volumeSizeGb, setVolumeSizeGb] = useState(20);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    if (!selectedOrganization) return;
    try {
      setError(null);
      setItems(await listDevelopmentWorkspaces(selectedOrganization.id));
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Unable to load workspaces.");
    }
  }, [selectedOrganization?.id]);

  useEffect(() => { void refresh(); }, [refresh]);

  const create = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!selectedOrganization || busy) return;
    setBusy(true);
    try {
      await createDevelopmentWorkspace({
        organizationId: selectedOrganization.id,
        name, imageId, profile, planId, volumeSizeGb,
        idempotencyKey: crypto.randomUUID(),
      });
      setName("");
      await refresh();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Unable to create workspace.");
    } finally {
      setBusy(false);
    }
  };

  const mutate = async (item: DevelopmentWorkspace, action: "start" | "stop" | "cancel-idle-stop") => {
    if (!selectedOrganization || busy) return;
    setBusy(true);
    try {
      await mutateDevelopmentWorkspace(item.id, selectedOrganization.id, action);
      await refresh();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : `Unable to ${action} workspace.`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <PageScaffold title="Development workspaces">
      <p className="text-sm text-muted-foreground">
        Disposable compute uses a retained volume. Stopped workspaces may continue to incur storage charges.
        Starts are explicit; previews and health checks never allocate a VM.
      </p>
      {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
      <Card>
        <CardHeader><CardTitle>Create workspace</CardTitle></CardHeader>
        <CardContent>
          <form className="grid gap-3 md:grid-cols-2" onSubmit={(event) => void create(event)}>
            <div className="space-y-2"><Label htmlFor="workspace-name">Name</Label><Input id="workspace-name" value={name} onChange={(event) => setName(event.target.value)} required maxLength={128} /></div>
            <div className="space-y-2"><Label htmlFor="workspace-image">Registered image ID</Label><Input id="workspace-image" value={imageId} onChange={(event) => setImageId(event.target.value)} required /></div>
            <div className="space-y-2"><Label htmlFor="workspace-profile">Profile</Label>
              <select id="workspace-profile" className="h-9 w-full rounded-md border bg-background px-3 text-sm" value={profile} onChange={(event) => setProfile(event.target.value)}>
                <option>Standard</option><option>Heavy</option>
              </select>
            </div>
            <div className="space-y-2"><Label htmlFor="workspace-plan">Approved plan ID</Label><Input id="workspace-plan" value={planId} onChange={(event) => setPlanId(event.target.value)} required /></div>
            <div className="space-y-2"><Label htmlFor="workspace-volume">Retained volume (GB)</Label><Input id="workspace-volume" type="number" min={10} max={10000} value={volumeSizeGb} onChange={(event) => setVolumeSizeGb(Number(event.target.value))} required /></div>
            <div className="flex items-end"><Button type="submit" disabled={busy}>{busy ? "Working…" : "Create stopped workspace"}</Button></div>
          </form>
        </CardContent>
      </Card>
      <section aria-label="Workspace list" className="space-y-3">
        {items.length === 0 ? <p className="text-sm text-muted-foreground">No development workspaces.</p> : items.map((item) => (
          <Card key={item.id}>
            <CardHeader><CardTitle>{item.name}</CardTitle></CardHeader>
            <CardContent className="space-y-3">
              <p>{item.desiredState} / {item.phase} · generation {item.generation} · {item.profile} / {item.sku} / {item.region}</p>
              <p className="text-sm text-muted-foreground">
                {item.volumeSizeGb} GB retained · estimated {(item.estimatedMonthlyCompute + item.estimatedMonthlyStorage).toFixed(2)} {item.currency}/month
                {item.pricingUpdatedAt ? ` (rates ${new Date(item.pricingUpdatedAt).toLocaleDateString()})` : " (pricing timestamp unavailable)"}
              </p>
              {item.shutdownDeadline ? <p>Idle stop deadline: {new Date(item.shutdownDeadline).toLocaleString()}</p> : null}
              {item.errorCode ? <p role="alert">{item.errorCode}: {item.errorMessage}</p> : null}
              <div className="flex flex-wrap gap-2">
                {item.desiredState === "stopped"
                  ? <Button disabled={busy} onClick={() => void mutate(item, "start")}>Start compute</Button>
                  : <Button variant="outline" disabled={busy} onClick={() => void mutate(item, "stop")}>Stop compute</Button>}
                {item.phase === "draining" && item.shutdownDeadline
                  ? <Button variant="outline" disabled={busy} onClick={() => void mutate(item, "cancel-idle-stop")}>Cancel idle stop</Button>
                  : null}
              </div>
            </CardContent>
          </Card>
        ))}
      </section>
    </PageScaffold>
  );
}
