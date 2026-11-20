import { Card, CardContent } from "@/components/ui/card";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceCodingAgents } from "./WorkspaceCodingAgents";
import { WorkspaceDeleteEnvironment } from "./WorkspaceDeleteEnvironment";
import { WorkspaceIdleTimeoutSettings } from "./WorkspaceIdleTimeoutSettings";
import { WorkspaceT3Connect } from "./WorkspaceT3Connect";
import { vmStartup } from "./WorkspaceVmStartup";
import type { permanentDeletion } from "./workspaceCollection";
import type { WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  // False once permanent deletion is requested.
  canManage: boolean;
  // The member's role allows management; deletion stays retryable after a failed attempt.
  canManageRole: boolean;
  canManageConnect: boolean;
  deletion: ReturnType<typeof permanentDeletion>;
  actorId?: string;
};

export function WorkspaceSettingsTab({
  item, organizationId, refresh, refreshAfterMutation, canManage, canManageRole, canManageConnect, deletion, actorId,
}: Props) {
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const startup = primary && vmStartup(primary);
  const showConnect = primary?.kind === "primary" && canManage && canManageConnect && !startup?.starting && !startup?.failed;
  return <div className="space-y-6">
    <WorkspaceIdleTimeoutSettings item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
    {showConnect && <Card><CardContent className="p-6">
      <WorkspaceT3Connect key={actorId ?? ""} workspaceId={item.id} vmId={primary.id} organizationId={organizationId}
        generation={primary.generation} revision={primary.revision} ready={!!startup?.ready} />
    </CardContent></Card>}
    <WorkspaceCodingAgents agents={item.agents} />
    {canManageRole && deletion !== "deleted" && <WorkspaceDeleteEnvironment item={item} organizationId={organizationId}
      vms={vms} failed={deletion === "failed"} deleting={deletion === "deleting"} refresh={refresh} />}
  </div>;
}
