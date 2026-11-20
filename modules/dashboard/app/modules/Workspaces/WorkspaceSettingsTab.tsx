import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceIdleTimeoutSettings } from "./WorkspaceIdleTimeoutSettings";
import type { WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  canManage: boolean;
};

export function WorkspaceSettingsTab({ item, organizationId, refreshAfterMutation, canManage }: Props) {
  return <WorkspaceIdleTimeoutSettings item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />;
}
