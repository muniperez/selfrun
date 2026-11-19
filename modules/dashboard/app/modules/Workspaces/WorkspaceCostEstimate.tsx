export const hoursPerMonth = 720;

export function formatPrice(amount: number | null | undefined, currency: string | null | undefined) {
  if (typeof amount !== "number" || !Number.isFinite(amount)) return null;
  try {
    return new Intl.NumberFormat(undefined, {
      style: "currency", currency: currency || "USD", minimumFractionDigits: 2, maximumFractionDigits: amount > 0 && amount < 1 ? 4 : 2,
    }).format(amount);
  } catch {
    return `${amount.toFixed(2)} ${currency ?? ""}`.trim();
  }
}

type Props = {
  computeHourlyRate: number | null | undefined;
  monthlyStorage: number | null | undefined;
  currency: string | null | undefined;
  className?: string;
};

/** Informational projection only: the server never rejects creation or startup because of it. */
export function WorkspaceCostEstimate({ computeHourlyRate, monthlyStorage, currency, className }: Props) {
  const hourly = formatPrice(computeHourlyRate, currency);
  const compute = typeof computeHourlyRate === "number" ? computeHourlyRate * hoursPerMonth : null;
  const storage = formatPrice(monthlyStorage, currency);
  const total = compute !== null && typeof monthlyStorage === "number" ? formatPrice(compute + monthlyStorage, currency) : null;
  return <div role="group" aria-label="Projected monthly cost" className={className ?? "space-y-2 text-sm"}>
    <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[auto_minmax(0,1fr)]">
      <dt className="text-muted-foreground">VM rate</dt>
      <dd>{hourly ? `${hourly}/hour while running` : "Not reported"}</dd>
      <dt className="text-muted-foreground">Storage</dt>
      <dd>{storage ? `${storage}/month, even while the VM is stopped` : "Not reported"}</dd>
      <dt className="text-muted-foreground">Projected monthly total</dt>
      <dd className="font-medium">{total ? `${total} if the VM runs ${hoursPerMonth} hours this month` : "Not available"}</dd>
    </dl>
    <p className="text-xs text-muted-foreground">
      Estimate from your operator's current prices, for planning only. You pay for the hours the VM actually runs; the estimate never blocks creating or starting a VM.
    </p>
  </div>;
}
