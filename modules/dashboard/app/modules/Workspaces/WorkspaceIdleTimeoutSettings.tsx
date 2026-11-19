import { useId, useState, type FormEvent } from "react";
import { updateWorkspaceIdleTimeout } from "@/api/workspacesApi";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { DevelopmentWorkspace } from "@/models/controlPlane";

const minMinutes = 1;
const maxMinutes = 10_080;

function duration(minutes: number) {
  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  const hourLabel = `${hours} ${hours === 1 ? "hour" : "hours"}`;
  const minuteLabel = `${remainder} ${remainder === 1 ? "minute" : "minutes"}`;
  return hours === 0 ? minuteLabel : remainder === 0 ? hourLabel : `${hourLabel} ${minuteLabel}`;
}

export function WorkspaceIdleTimeoutSettings({ item, organizationId, canManage, refresh }: {
  item: DevelopmentWorkspace;
  organizationId: string;
  canManage: boolean;
  refresh: () => Promise<void>;
}) {
  const inputId = useId();
  const [draft, setDraft] = useState<string | null>(null);
  const [saved, setSaved] = useState<DevelopmentWorkspace | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const current = saved && saved.revision > item.revision ? saved : item;
  const effective = current.effectiveIdleTimeoutMinutes;
  const explicit = current.idleTimeoutMinutes;
  const value = draft ?? (explicit?.toString() ?? "");
  const canEdit = canManage && item.organizationId === organizationId && effective != null;

  const save = async (minutes: number | null) => {
    if (!canEdit || saving) return;
    setSaving(true);
    setConfirmed(false);
    setError(null);
    try {
      setSaved(await updateWorkspaceIdleTimeout(item.id, organizationId, minutes));
      setDraft(null);
      setConfirmed(true);
      await refresh();
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Unable to update idle timeout.");
    } finally {
      setSaving(false);
    }
  };

  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const minutes = Number(value);
    if (!/^\d+$/.test(value) || minutes < minMinutes || minutes > maxMinutes) {
      setError(`Enter a whole number from ${minMinutes} to ${maxMinutes.toLocaleString()} minutes.`);
      setConfirmed(false);
      return;
    }
    void save(minutes);
  };

  return <Card>
    <CardHeader><CardTitle>Idle timeout</CardTitle></CardHeader>
    <CardContent className="space-y-4 text-sm">
      <p>Effective timeout: <strong>{effective == null ? "Unavailable" : duration(effective)}</strong>
        {effective != null && <> ({explicit == null ? "server default" : "workspace setting"})</>}.</p>
      <p className="text-muted-foreground">Each primary and ephemeral VM uses this timeout independently. It is the time
        without activity before automatic shutdown can begin, not a maximum VM lifetime. The shutdown countdown and gateway drain are separate.</p>
      <p className="text-muted-foreground">Idle shutdown deletes VM compute and retains primary storage. Compute charges
        continue until deletion is confirmed, and a longer timeout may increase cost.</p>
      {canEdit && <form onSubmit={submit} className="space-y-3">
        <Label htmlFor={inputId}>Idle timeout (minutes)</Label>
        <Input id={inputId} inputMode="numeric" value={value} placeholder={String(effective)}
          aria-describedby={`${inputId}-range`} className="max-w-40" disabled={saving}
          onChange={event => { setDraft(event.target.value); setError(null); setConfirmed(false); }} />
        <p id={`${inputId}-range`} className="text-muted-foreground">Enter 1 to 10,080 whole minutes (up to 7 days).</p>
        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={saving}>{saving ? "Saving..." : "Save idle timeout"}</Button>
          {explicit != null && <Button type="button" variant="outline" disabled={saving}
            onClick={() => void save(null)}>Use server default</Button>}
        </div>
      </form>}
      {error && <p role="alert" className="text-destructive">{error}</p>}
      {confirmed && <p role="status">Idle timeout saved.</p>}
    </CardContent>
  </Card>;
}
