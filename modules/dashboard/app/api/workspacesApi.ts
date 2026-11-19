import { apiRequest } from "./apiClient";
import type { DevelopmentWorkspace, DevelopmentWorkspaceTaskLease } from "@/models/controlPlane";

export type WorkspaceResize = { generation: number; imageId: string; profile: string; planId: string };
export type EphemeralLaunch = {
  imageId: string; profile: string; planId: string; repository: string; baseRef: string; baseCommit: string;
  pushAuthorized: boolean; acknowledgeIgnoredDataLoss: boolean; idempotencyKey: string; repositoryCredentialRef?: string;
};
export type VmAction = "start" | "stop" | "cancel-idle-stop" | "discard" | "resize";

function workspacePath(id: string, organizationId: string, operation: string) {
  return `/api/development-workspaces/${encodeURIComponent(id)}/${operation}?organizationId=${encodeURIComponent(organizationId)}`;
}

export function getWorkspace(id: string, organizationId: string, signal?: AbortSignal) {
  return apiRequest<DevelopmentWorkspace>(
    `/api/development-workspaces/${encodeURIComponent(id)}?organizationId=${encodeURIComponent(organizationId)}`, { signal },
  );
}

export function deleteWorkspace(id: string, organizationId: string) {
  return apiRequest<DevelopmentWorkspace>(
    `/api/development-workspaces/${encodeURIComponent(id)}?organizationId=${encodeURIComponent(organizationId)}&confirmPermanentDeletion=true`,
    { method: "DELETE" },
  );
}

export function workspaceOperation(id: string, organizationId: string, operation: string, body?: unknown) {
  return apiRequest<DevelopmentWorkspace>(workspacePath(id, organizationId, operation), {
    method: "POST", ...(body === undefined ? {} : { body }),
  });
}

export function updateWorkspaceIdleTimeout(id: string, organizationId: string, idleTimeoutMinutes: number | null,
  keepRunning = false) {
  return apiRequest<DevelopmentWorkspace>(workspacePath(id, organizationId, "idle-timeout"), {
    method: "PUT", body: { idleTimeoutMinutes, keepRunning },
  });
}

export function launchEphemeralVm(id: string, organizationId: string, request: EphemeralLaunch) {
  return workspaceOperation(id, organizationId, "vms/ephemeral", request);
}

export function mutateWorkspaceVm(id: string, vmId: string, organizationId: string, action: VmAction,
  request: { generation: number; discardLocalData?: boolean; acknowledgeIgnoredDataLoss?: boolean } | WorkspaceResize) {
  return workspaceOperation(id, organizationId, `vms/${encodeURIComponent(vmId)}/${action}`, request);
}

export function workspaceTask(id: string, vmId: string, organizationId: string, taskId: string,
  generation: number, complete: boolean, cancelled = false) {
  return apiRequest<DevelopmentWorkspaceTaskLease>(
    workspacePath(id, organizationId, `vms/${encodeURIComponent(vmId)}/tasks/${encodeURIComponent(taskId)}/${complete ? "complete" : "lease"}`),
    { method: complete ? "POST" : "PUT", body: { generation, ...(complete ? { cancelled } : {}) } },
  );
}

export function getWorkspaceLaunchUrl(id: string, vmId: string, organizationId: string, generation: number, service = "t3") {
  return apiRequest<{ url: string; expiresAt: string }>(
    workspacePath(id, organizationId, `vms/${encodeURIComponent(vmId)}/launch`),
    { method: "POST", body: { generation, service } },
  );
}

export type WorkspaceSelectionOption = {
  imageId: string;
  imageName: string;
  profile: string;
  planId: string;
  planName: string;
  providerAccountName: string;
  region: string;
  sku: string;
  cpuCount?: number;
  memoryGiB?: number;
  computeHourlyRate?: number;
  isDefault?: boolean;
  agents?: WorkspaceAgent[] | null;
};

export type WorkspaceAgent = { id: string; name: string; version: string };

export type WorkspaceSelections = {
  available: boolean;
  unavailableReason?: string | null;
  options: WorkspaceSelectionOption[];
  currency?: string | null;
  volumeMonthlyRatePerGb?: number | null;
};

export function getWorkspaceSelections(organizationId: string, workspaceId?: string,
  signal?: AbortSignal): Promise<WorkspaceSelections> {
  const query = new URLSearchParams({ organizationId });
  if (workspaceId !== undefined) query.set("workspaceId", workspaceId);
  return apiRequest<WorkspaceSelections>(`/api/development-workspaces/selections?${query}`, { signal });
}
