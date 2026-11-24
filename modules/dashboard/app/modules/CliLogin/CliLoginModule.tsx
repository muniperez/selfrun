import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { CheckCircle2, Loader2, Terminal } from "lucide-react";
import { useEffect, useState } from "react";
import { Navigate, useSearchParams } from "react-router-dom";
import { approveCliLoginSession, getCliLoginSession } from "../../api/cliLoginApi";
import { AuthenticatedArea } from "../../core/auth/AuthenticatedArea";
import { useAuth } from "../../core/auth/AuthProvider";
import type { CliLoginSessionResponse } from "../../models/controlPlane";

export function CliLoginModule() {
  const [searchParams] = useSearchParams();
  const sessionId = searchParams.get("session");

  return (
    <AuthenticatedArea>
      {sessionId ? <CliLoginContent key={sessionId} sessionId={sessionId} /> : <Navigate replace to="/apps" />}
    </AuthenticatedArea>
  );
}

function CliLoginContent({ sessionId }: { sessionId: string }) {
  const { profile, profileError } = useAuth();
  const eligibleOrganizations = profile?.organizations.filter(organization => ["owner", "admin"].includes(organization.role)) ?? [];
  const [organizationId, setOrganizationId] = useState<string | null>(() =>
    eligibleOrganizations.length === 1 ? eligibleOrganizations[0].id : null
  );
  const organization = eligibleOrganizations.find(candidate => candidate.id === organizationId);
  const [session, setSession] = useState<CliLoginSessionResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isApproving, setIsApproving] = useState(false);

  useEffect(() => {
    let active = true;
    getCliLoginSession(sessionId)
      .then(result => { if (active) setSession(result); })
      .catch((loadError) => {
        if (active) setError(loadError instanceof Error ? loadError.message : "Unable to load CLI login session.");
      })
      .finally(() => { if (active) setIsLoading(false); });
    return () => { active = false; };
  }, [sessionId]);

  const isApproved = session?.status === "approved" || session?.status === "exchanged";
  const isExpired = session?.status === "expired";
  const canApprove = !!organization && !profileError && !isLoading && session?.status === "pending" && !isApproving;

  async function approve() {
    if (!canApprove || !organization) {
      return;
    }

    setIsApproving(true);
    setError(null);
    try {
      setSession(await approveCliLoginSession(sessionId, organization.id));
    } catch (approveError) {
      setError(approveError instanceof Error ? approveError.message : "Unable to approve CLI login.");
    } finally {
      setIsApproving(false);
    }
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background px-4 py-8">
      <Card className="w-full max-w-lg">
        <CardHeader>
          <div className="mb-2 flex h-10 w-10 items-center justify-center rounded-md bg-primary text-primary-foreground">
            {isApproved ? <CheckCircle2 className="h-5 w-5" /> : <Terminal className="h-5 w-5" />}
          </div>
          <CardTitle>Selfrun CLI login</CardTitle>
          <CardDescription className="break-words">
            {isApproved ? "This CLI request has been authorized." : organization ? `Authorize CLI access for ${organization.name}.` : "Choose an organization before approving CLI access."}
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {!isApproved && !isExpired && eligibleOrganizations.length > 0 ? (
            <div className="space-y-2">
              <label className="block text-sm font-medium" htmlFor="cli-organization">Organization</label>
              <select
                id="cli-organization"
                className="h-10 w-full min-w-0 rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"
                value={organization?.id ?? ""}
                onChange={event => setOrganizationId(event.target.value || null)}
                disabled={isLoading || isApproving || !!profileError}
                aria-describedby="cli-organization-help"
              >
                <option value="" disabled>Choose an organization</option>
                {eligibleOrganizations.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name} ({candidate.slug})</option>)}
              </select>
              <p id="cli-organization-help" className="text-sm text-muted-foreground">
                CLI access applies only to the chosen organization. Only owner and admin memberships are available.
              </p>
              {organizationId && !organization ? <p role="alert" className="text-sm text-destructive">Your chosen organization is no longer eligible. Choose an organization again.</p> : null}
            </div>
          ) : null}
          {session ? (
            <div className="break-words rounded-md border border-border p-3 text-sm">
              <div>Status: {session.status}</div>
              <div>Expires: {formatDate(session.expiresAt)}</div>
              <div>Scopes: {session.scopes.join(", ")}</div>
            </div>
          ) : isLoading ? (
            <div role="status" className="flex items-center text-sm text-muted-foreground">
              <Loader2 className="mr-2 h-4 w-4 animate-spin" />
              Loading CLI request
            </div>
          ) : null}
          {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
          {profileError ? <p role="alert" className="text-sm text-destructive">{profileError}</p> : null}
          {!profile ? <p role="status">Loading organization memberships.</p> : eligibleOrganizations.length === 0 ? <p role="alert">You do not have permission to approve CLI access.</p> : null}
          {isApproved ? <p role="status" className="text-sm text-muted-foreground">You can return to your terminal.</p> : null}
          {isExpired ? <p role="alert" className="text-sm text-destructive">This CLI login session expired. Run `selfrun login` again.</p> : null}
          {!isApproved && !isExpired ? (
            <Button className="w-full" disabled={!canApprove} onClick={approve}>
              {isApproving ? <Loader2 className="h-4 w-4 animate-spin" /> : <Terminal className="h-4 w-4" />}
              Approve CLI login
            </Button>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}
