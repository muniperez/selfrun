import { useEffect, useId, useRef, useState } from "react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { getWorkspaceSelections, type WorkspaceSelectionOption } from "@/api/workspacesApi";

export type WorkspaceSelection = { imageId: string; profile: string; planId: string };

type Props = {
  organizationId: string;
  workspaceId?: string;
  value: WorkspaceSelection;
  onChange: (value: WorkspaceSelection) => void;
  disabled?: boolean;
  onReadyChange?: (ready: boolean) => void;
};
type Catalog = {
  scope: string;
  available: boolean;
  unavailableReason?: string | null;
  options: WorkspaceSelectionOption[];
};
const selectClass = "h-10 w-full min-w-0 rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50";

function compatible(value: WorkspaceSelection, options: WorkspaceSelectionOption[]): WorkspaceSelection {
  const images = options.filter(option => option.imageId === value.imageId);
  if (!images.length) return { imageId: "", profile: "", planId: "" };
  const profiles = images.filter(option => option.profile === value.profile);
  if (!profiles.length) return { ...value, profile: "", planId: "" };
  return profiles.some(option => option.planId === value.planId) ? value : { ...value, planId: "" };
}

export function WorkspaceSelectionFields({ organizationId, workspaceId, value, onChange, disabled = false, onReadyChange }: Props) {
  const id = useId();
  const scope = JSON.stringify([organizationId, workspaceId]);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [revision, setRevision] = useState(0);
  const callbacks = useRef({ onChange, onReadyChange });
  callbacks.current = { onChange, onReadyChange };

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    callbacks.current.onReadyChange?.(false);
    void getWorkspaceSelections(organizationId, workspaceId, controller.signal).then(result => {
      if (!controller.signal.aborted) setCatalog({ ...result, scope });
    }).catch(reason => {
      if (!controller.signal.aborted) {
        setCatalog(null);
        setError(reason instanceof Error ? reason.message : "Couldn't load options.");
      }
    }).finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [organizationId, workspaceId, scope, revision]);

  const current = catalog?.scope === scope ? catalog : null;
  const pending = loading || !current && !error;
  const options = !pending && !error && current?.available ? current.options : [];
  const normalized = compatible(value, options);
  const ready = !pending && !error && !!current?.available && !!value.imageId && !!value.profile && !!value.planId &&
    normalized.imageId === value.imageId && normalized.profile === value.profile && normalized.planId === value.planId;

  useEffect(() => {
    callbacks.current.onReadyChange?.(ready);
  }, [ready]);
  useEffect(() => {
    if (!pending && !error && current && (
      normalized.imageId !== value.imageId || normalized.profile !== value.profile || normalized.planId !== value.planId
    )) callbacks.current.onChange(normalized);
  }, [pending, error, current, normalized.imageId, normalized.profile, normalized.planId, value.imageId, value.profile, value.planId]);

  const images = [...new Map(options.map(option => [option.imageId, option])).values()];
  const profiles = [...new Set(options.filter(option => option.imageId === value.imageId).map(option => option.profile))];
  const plans = [...new Map(options.filter(option => option.imageId === value.imageId && option.profile === value.profile)
    .map(option => [option.planId, option])).values()];
  const unavailable = !pending && !error && current && !current.available;
  const selectedAgents = options.find(option => option.imageId === value.imageId && option.agents?.length)?.agents;
  return <div className="space-y-4">
    <div className="space-y-2">
      <Label htmlFor={`${id}-image`}>Image</Label>
      <select id={`${id}-image`} className={selectClass} required disabled={disabled || pending || !images.length}
        value={value.imageId} onChange={event => onChange(compatible({ ...value, imageId: event.target.value }, options))}>
        <option value="">Select image</option>
        {images.map(option => <option key={option.imageId} value={option.imageId}>{option.imageName}</option>)}
      </select>
      {!!selectedAgents?.length && <p className="text-xs text-muted-foreground">
        {`Agents: ${selectedAgents.map(agent => `${agent.name} ${agent.version}`).join(", ")}`}
      </p>}
    </div>
    <div className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_minmax(0,1.6fr)]">
      <div className="space-y-2">
        <Label htmlFor={`${id}-profile`}>Size</Label>
        <select id={`${id}-profile`} className={selectClass} required disabled={disabled || pending || !profiles.length}
          value={value.profile} onChange={event => onChange(compatible({ ...value, profile: event.target.value }, options))}>
          <option value="">Select size</option>
          {profiles.map(profile => <option key={profile}>{profile}</option>)}
        </select>
      </div>
      <div className="space-y-2">
        <Label htmlFor={`${id}-plan`}>Plan</Label>
        <select id={`${id}-plan`} className={selectClass} required disabled={disabled || pending || !plans.length}
          value={value.planId} onChange={event => onChange({ ...value, planId: event.target.value })}>
          <option value="">Select plan</option>
          {plans.map(option => <option key={option.planId} value={option.planId}>
            {option.planName} · {option.providerAccountName} · {option.region} · {option.sku}
          </option>)}
        </select>
      </div>
    </div>
    {pending && <p role="status" className="text-sm text-muted-foreground">Loading options…</p>}
    {error && <p role="alert" className="text-sm text-destructive">Couldn't load options. {error}</p>}
    {unavailable && <p role="status" className="text-sm text-muted-foreground">
      {current.unavailableReason || "Dev environments are turned off. Ask an administrator to turn them on."}
    </p>}
    {!pending && !error && current?.available && !options.length && <p role="status" className="text-sm text-muted-foreground">
      No matching options. Ask an administrator to approve an image and a matching plan, then reload.
    </p>}
    <Button type="button" size="sm" variant="outline" disabled={disabled || pending} onClick={() => {
      setLoading(true);
      callbacks.current.onReadyChange?.(false);
      setRevision(previous => previous + 1);
    }}>{error ? "Retry" : "Reload options"}</Button>
  </div>;
}
