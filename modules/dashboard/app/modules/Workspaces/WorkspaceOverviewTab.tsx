import { Fragment, useId, useState } from "react";
import { ChevronDown, ChevronRight, GitBranch } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { WorkspaceLaunchEphemeralDialog } from "./WorkspaceLaunchEphemeralDialog";
import { WorkspaceVmCard } from "./WorkspaceVmCard";
import { vmStartup } from "./WorkspaceVmStartup";
import { phaseLabel, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  // Reports refresh failures after an accepted mutation instead of throwing.
  refreshAfterMutation: WorkspaceRefresh;
  canManage: boolean;
  canManageConnect: boolean;
  actorId?: string;
};

export function WorkspaceOverviewTab({ item, organizationId, refresh, refreshAfterMutation, canManage, canManageConnect, actorId }: Props) {
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const ephemeral = vms.filter(vm => vm.kind === "ephemeral" && vm.id !== primary?.id);
  return <>
    <section aria-label="Primary VM">
      {primary ? <WorkspaceVmCard key={`${primary.id}:${primary.generation}:${actorId ?? ""}`} workspaceId={item.id} organizationId={organizationId}
        vm={primary} currency={item.currency} refresh={refresh} canManage={canManage} canManageConnect={canManageConnect} variant="primary"
        retainedStorage={{ sizeGb: item.volumeSizeGb, status: item.volumeStatus, estimatedMonthlyStorage: item.estimatedMonthlyStorage }} />
        : <Card><CardContent className="space-y-2 p-6 text-sm">
        <p className="font-medium">No primary VM reported.</p>
        <p className="text-muted-foreground">Select Refresh status to load it. A temporary VM is never used instead. Storage: {item.volumeSizeGb} GB · {item.volumeStatus ?? "status unavailable"}.</p>
      </CardContent></Card>}
    </section>
    <section aria-labelledby="ephemeral-heading" className="min-w-0 space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="ephemeral-heading" className="text-lg font-semibold">Temporary VMs</h2>
          <p className="text-sm text-muted-foreground">Each temporary VM works on its own Git branch with its own disk, and starts and stops on its own.</p>
        </div>
        <WorkspaceLaunchEphemeralDialog item={item} organizationId={organizationId} refresh={refreshAfterMutation} canManage={canManage} />
      </div>
      <EphemeralTable vms={ephemeral} workspaceId={item.id} organizationId={organizationId}
        currency={item.currency} refresh={refresh} canManage={canManage} actorId={actorId} />
    </section>
  </>;
}

function EphemeralTable({ vms, workspaceId, organizationId, currency, refresh, canManage, actorId }: {
  vms: DevelopmentWorkspaceVm[]; workspaceId: string; organizationId: string; currency: string;
  refresh: WorkspaceRefresh; canManage: boolean; actorId?: string;
}) {
  return <div className="overflow-hidden rounded-lg border">
    <Table aria-label="Temporary VMs">
      <TableHeader><TableRow>
        <TableHead scope="col">Work branch / repository</TableHead>
        <TableHead scope="col">Status</TableHead><TableHead scope="col">Git save</TableHead>
        <TableHead scope="col"><span className="sr-only">Details</span></TableHead>
      </TableRow></TableHeader>
      <TableBody>
        {vms.map(vm => <EphemeralRow key={`${vm.id}:${vm.generation}:${actorId ?? ""}`} vm={vm} workspaceId={workspaceId}
          organizationId={organizationId} currency={currency} refresh={refresh} canManage={canManage} />)}
        {!vms.length && <TableRow><TableCell colSpan={4} className="space-y-1 py-10 text-center">
          <p className="font-medium">No temporary VMs</p>
          <p className="text-muted-foreground">Launch a separate Git-only VM when you need parallel work. Viewing this page never starts a VM.</p>
        </TableCell></TableRow>}
      </TableBody>
    </Table>
  </div>;
}

function EphemeralRow({ vm, workspaceId, organizationId, currency, refresh, canManage }: {
  vm: DevelopmentWorkspaceVm; workspaceId: string; organizationId: string; currency: string;
  refresh: WorkspaceRefresh; canManage: boolean;
}) {
  const { starting, failed, ready } = vmStartup(vm);
  const [expanded, setExpanded] = useState(starting || failed);
  const [visited, setVisited] = useState(starting || failed);
  const detailsId = useId();
  return <Fragment>
    <TableRow>
      <TableCell className="min-w-48">
        <div className="flex items-start gap-3 py-2">
          <GitBranch aria-hidden="true" className="mt-1 h-4 w-4 shrink-0 text-muted-foreground" />
          <div className="min-w-0">
            <p className="break-all font-medium">{vm.workBranch ?? "Work branch pending"}</p>
            <p className="break-all text-xs text-muted-foreground">{vm.repository ?? "Repository unavailable"}</p>
          </div>
        </div>
      </TableCell>
      <TableCell><Badge role={expanded ? undefined : "status"} variant={failed || vm.errorCode && vm.phase !== "awaiting_git_save" ? "destructive" : "secondary"}
        className={vm.phase === "awaiting_git_save" ? "border-warning/30 bg-warning-surface text-warning"
          : ready ? "border-success/30 bg-success-surface text-success" : undefined}>{phaseLabel(vm)}</Badge></TableCell>
      <TableCell>{vm.gitStatus.replaceAll("_", " ").replace(/^./, character => character.toUpperCase())}</TableCell>
      <TableCell>
        <Button size="icon" variant="ghost" aria-label={`${expanded ? "Collapse" : "Expand"} VM ${vm.id}`}
          aria-expanded={expanded} aria-controls={detailsId} onClick={() => { setExpanded(!expanded); setVisited(true); }}>
          {expanded ? <ChevronDown aria-hidden="true" className="h-4 w-4" /> : <ChevronRight aria-hidden="true" className="h-4 w-4" />}
        </Button>
      </TableCell>
    </TableRow>
    <TableRow hidden={!expanded}>
      <TableCell colSpan={4} className={vm.phase === "awaiting_git_save" ? "border-t border-warning/30 bg-warning-surface p-0" : "bg-muted/20 p-0"}>
        <div id={detailsId} role="region" aria-label={`Details for VM ${vm.id}`} className="min-w-0">
          {visited && <WorkspaceVmCard workspaceId={workspaceId} organizationId={organizationId} vm={vm}
            currency={currency} refresh={refresh} canManage={canManage} variant="embedded" />}
        </div>
      </TableCell>
    </TableRow>
  </Fragment>;
}
