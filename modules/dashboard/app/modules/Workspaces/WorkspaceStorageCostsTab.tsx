import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { DevelopmentWorkspace, DevelopmentWorkspaceVm } from "@/models/controlPlane";
import { hoursPerMonth, WorkspaceCostEstimate } from "./WorkspaceCostEstimate";
import { WorkspaceDiskUsage } from "./WorkspaceDiskUsage";
import { vmStartup } from "./WorkspaceVmStartup";
import { phaseLabel, type WorkspaceRefresh } from "./workspaceVmLabels";

type Props = {
  item: DevelopmentWorkspace;
  organizationId: string;
  refresh: WorkspaceRefresh;
  canManage: boolean;
};


export function WorkspaceStorageCostsTab({ item, organizationId, refresh, canManage }: Props) {
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
  const vmLabel = (vm: DevelopmentWorkspaceVm) => vm.id === primary?.id ? "Primary VM" : vm.workBranch ?? "Temporary VM";
  const ordered = [...(primary ? [primary] : []), ...vms.filter(vm => vm.id !== primary?.id)];
  // Disk tools stay scoped to each VM's own disk and only appear where that VM reports supported resources.
  const diskVms = canManage ? ordered.filter(vm => vm.resources && vm.resources.state !== "unsupported") : [];
  return <>
    <Card>
      <CardHeader><CardTitle>Storage</CardTitle></CardHeader>
      <CardContent className="space-y-4">
        <dl className="grid gap-4 text-sm sm:grid-cols-2 lg:grid-cols-4">
          <Metric label="Size" value={`${item.volumeSizeGb} GB`} />
          <Metric label="Status" value={item.volumeStatus ?? "Status unavailable"} />
          <Metric label="Estimated cost per month" value={`${item.estimatedMonthlyStorage.toFixed(2)} ${item.currency}`} />
          <Metric label="Prices last updated" value={item.pricingUpdatedAt ?? "Unavailable"} />
        </dl>
        <p className="text-sm text-muted-foreground">Storage is charged even when all VMs are stopped. Only files on this storage are kept when the primary VM is replaced; other disks, including every temporary VM's disk, are deleted. Storage isn't a backup.</p>
      </CardContent>
    </Card>
    {primary && <section aria-labelledby="cost-estimate-heading" className="space-y-2">
      <h2 id="cost-estimate-heading" className="text-lg font-semibold">Projected cost</h2>
      <WorkspaceCostEstimate computeHourlyRate={primary.computeHourlyRate} monthlyStorage={item.estimatedMonthlyStorage}
        currency={item.currency} className="space-y-2 rounded-md border p-4 text-sm" />
    </section>}
    <section aria-label="VM costs" className="space-y-3">
      <h2 className="text-lg font-semibold">VM costs</h2>
      <div className="overflow-hidden rounded-lg border">
        <Table>
          <TableHeader><TableRow>
            <TableHead scope="col">VM</TableHead><TableHead scope="col">Status</TableHead>
            <TableHead scope="col">Server</TableHead><TableHead scope="col">Per hour</TableHead>
            <TableHead scope="col">Per month if running {hoursPerMonth} hours</TableHead>
          </TableRow></TableHeader>
          <TableBody>
            {ordered.map(vm => <TableRow key={vm.id}>
              <TableCell className="break-words font-medium">{vmLabel(vm)}<span className="block text-xs text-muted-foreground">{vm.id}</span></TableCell>
              <TableCell>{phaseLabel(vm)}</TableCell>
              <TableCell>{vm.computeReserved ? "Allocated · charges may continue" : "Not allocated"}</TableCell>
              <TableCell className="whitespace-nowrap">{vm.computeHourlyRate.toFixed(4)} {item.currency}</TableCell>
              <TableCell className="whitespace-nowrap">{vm.estimatedMonthlyCompute.toFixed(2)} {item.currency}</TableCell>
            </TableRow>)}
            {!vms.length && <TableRow><TableCell colSpan={5} className="py-6 text-center text-muted-foreground">No VMs reported.</TableCell></TableRow>}
          </TableBody>
        </Table>
      </div>
      <p className="text-sm text-muted-foreground">These are estimates based on your operator's prices, not invoices or a billing guarantee. Currency: {item.currency}. Prices last updated: {item.pricingUpdatedAt ?? "unavailable"}.</p>
      <p className="text-sm text-muted-foreground">Cloud providers charge for a VM's server until it's deleted, not just powered off, and may round up to whole hours. Repeated short sessions may each cost an hour. Backups, snapshots, IPs, traffic and the control plane may cost extra.</p>
    </section>
    {diskVms.length > 0 && <section aria-labelledby="disk-usage-heading" className="space-y-3">
      <div>
        <h2 id="disk-usage-heading" className="text-lg font-semibold">Disk usage</h2>
        <p className="text-sm text-muted-foreground">Each VM has its own disk. Folder sizes and deletions apply only to the VM shown.</p>
      </div>
      {diskVms.map(vm => <Card key={vm.id} role="region" aria-label={`Disk tools for ${vmLabel(vm)}`} className="overflow-hidden">
        <CardHeader className="pb-3">
          <CardTitle className="break-words text-base">{vmLabel(vm)}</CardTitle>
          <p className="break-words text-xs text-muted-foreground">{vm.id} · {phaseLabel(vm)}</p>
        </CardHeader>
        <WorkspaceDiskUsage workspaceId={item.id} workspaceName={item.name} organizationId={organizationId} vm={vm}
          ready={vmStartup(vm).ready} refresh={() => refresh()} />
      </Card>)}
    </section>}
  </>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0 space-y-1"><dt className="text-muted-foreground">{label}</dt><dd className="break-words font-medium">{value}</dd></div>;
}
