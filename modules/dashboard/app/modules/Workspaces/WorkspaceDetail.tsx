import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useAuth } from "@/core/auth/AuthProvider";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { WorkspaceEnvironmentActions } from "./WorkspaceEnvironmentActions";
import { WorkspaceOverviewTab } from "./WorkspaceOverviewTab";
import { WorkspaceSettingsTab } from "./WorkspaceSettingsTab";
import { WorkspaceStorageCostsTab } from "./WorkspaceStorageCostsTab";
import { vmStartup } from "./WorkspaceVmStartup";
import { permanentDeletion } from "./workspaceCollection";
import { message, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  canManage: boolean;
};

export function WorkspaceDetail(props: Props) {
  return <ScopedWorkspaceDetail key={`${props.organizationId}:${props.item.id}`} {...props} />;
}

function ScopedWorkspaceDetail({ item, organizationId, refresh, canManage: canManageRole }: Props) {
  const { profile, selectedOrganization } = useAuth();
  const deletion = permanentDeletion(item);
  // Once permanent deletion is requested, only retrying deletion remains available.
  const canManage = canManageRole && !deletion;
  const canManageConnect = canManage && Boolean(profile?.id) && item.organizationId === organizationId &&
    selectedOrganization?.id === organizationId &&
    (selectedOrganization.role.toLowerCase() === "owner" || item.createdBy === profile?.id);
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);

  const refreshAfterMutation = async (observation?: DevelopmentWorkspace) => {
    setRefreshError(null);
    try { await refresh(observation); }
    catch (reason) { if (mounted.current) setRefreshError(`Operation accepted. Refresh failed: ${message(reason)}`); }
  };
  return <div className="min-w-0 space-y-6">
    {item.errorCode && !(primary && (vmStartup(primary).failed || primary.errorCode === item.errorCode)) && <p role="alert" className="text-sm text-destructive">{item.errorCode}: {item.errorMessage}</p>}
    {refreshError && <p role="alert" className="text-sm text-destructive">{refreshError}</p>}
    {deletion === "deleting" && <p role="status" className="rounded-md border border-warning bg-warning-surface p-3 text-sm text-warning">
      Deleting this dev environment and its saved files. Refresh to check progress.</p>}
    {deletion === "failed" && <p className="rounded-md border border-destructive p-3 text-sm text-destructive">
      Deleting this dev environment failed. Fix the reported problem, then retry from Delete environment in the Settings tab.</p>}
    {deletion === "deleted" && <p role="status" className="rounded-md border p-3 text-sm">
      This dev environment and its saved files were permanently deleted. <Link className="font-medium text-primary underline" to="/workspaces">Back to dev environments</Link></p>}
    {!canManageRole && <p className="text-sm text-muted-foreground">You can view this dev environment but can't change it.</p>}
    <Tabs defaultValue="overview" className="min-w-0">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b pb-3">
        <TabsList aria-label="Dev environment details" className="h-auto gap-6 rounded-none bg-transparent p-0">
          <TabsTrigger value="overview" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Overview</TabsTrigger>
          <TabsTrigger value="storage" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Storage &amp; costs</TabsTrigger>
          <TabsTrigger value="settings" className="rounded-none border-b-2 border-transparent px-0 py-2 data-[state=active]:border-foreground data-[state=active]:bg-transparent data-[state=active]:shadow-none">Settings</TabsTrigger>
        </TabsList>
        <WorkspaceEnvironmentActions item={item} organizationId={organizationId} refreshAfterMutation={refreshAfterMutation} canManage={canManage} />
      </div>
      <TabsContent value="overview" className="space-y-8 pt-4">
        <WorkspaceOverviewTab item={item} organizationId={organizationId} refresh={refresh} refreshAfterMutation={refreshAfterMutation}
          canManage={canManage} canManageConnect={canManageConnect} actorId={profile?.id} />
      </TabsContent>
      <TabsContent value="storage" className="space-y-6 pt-4">
        <WorkspaceStorageCostsTab item={item} organizationId={organizationId} refresh={refresh} canManage={canManage} />
      </TabsContent>
      <TabsContent value="settings" className="pt-4">
        <WorkspaceSettingsTab item={item} organizationId={organizationId} refresh={refresh} refreshAfterMutation={refreshAfterMutation}
          canManage={canManage} canManageRole={canManageRole} canManageConnect={canManageConnect} deletion={deletion} actorId={profile?.id} />
      </TabsContent>
    </Tabs>
  </div>;
}
