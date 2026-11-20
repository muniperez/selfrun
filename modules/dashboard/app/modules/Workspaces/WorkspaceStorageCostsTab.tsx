import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import type { DevelopmentWorkspace } from "@/models/controlPlane";
import { hoursPerMonth } from "./WorkspaceCostEstimate";
import { phaseLabel } from "./workspaceVmLabels";

export function WorkspaceStorageCostsTab({ item }: { item: DevelopmentWorkspace }) {
  const vms = item.vms ?? [];
  const primary = vms.find(vm => vm.id === item.primaryVmId) ?? vms.find(vm => vm.kind === "primary");
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
            {[...(primary ? [primary] : []), ...vms.filter(vm => vm.id !== primary?.id)].map(vm => <TableRow key={vm.id}>
              <TableCell className="break-words font-medium">{vm.kind === "primary" ? "Primary VM" : "Temporary VM"}<span className="block text-xs text-muted-foreground">{vm.id}</span></TableCell>
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
  </>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0 space-y-1"><dt className="text-muted-foreground">{label}</dt><dd className="break-words font-medium">{value}</dd></div>;
}
