import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useState } from "react";
import { setKeepSignedIn } from "../../api/accountApi";
import { useAuth } from "../../core/auth/AuthProvider";
import { ApiError } from "../../models/controlPlane";

const formatter = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

export function SessionSettingsCard() {
  const { capabilities, session, refreshSession } = useAuth();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  if (!session) return null;
  const offered = capabilities?.keepSignedIn === true;

  async function change(value: boolean) {
    setBusy(true);
    setError(null);
    try {
      await setKeepSignedIn(value);
    } catch (changeError) {
      setError(changeError instanceof ApiError && changeError.code === "keep_signed_in_unavailable"
        ? "Keep me signed in is disabled by the host administrator."
        : changeError instanceof ApiError && changeError.status === 401
          ? null
          : "Your sign-in preference could not be saved. Try again.");
    } finally {
      setBusy(false);
    }
    // Reload the session either way so deadlines, renewal and any sign-out are reflected.
    await refreshSession();
  }

  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>Sign-in session</CardTitle>
        <CardDescription>
          Signed in until {formatter.format(new Date(session.idleExpiresAt))} if inactive, and no later than{" "}
          {formatter.format(new Date(session.expiresAt))}.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {offered ? (
          <>
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-1 accent-primary" checked={session.keepSignedIn === true} disabled={busy}
                onChange={event => void change(event.target.checked)} />
              <span>Keep me signed in on this device</span>
            </label>
            <p className="text-sm text-muted-foreground">
              Also used as your default when you sign in. Turning it off applies the standard limits to this session
              and may sign you out if it is already older than they allow.
            </p>
          </>
        ) : (
          <p className="text-sm text-muted-foreground">Keep me signed in is not offered on this instance.</p>
        )}
        {error ? <p role="alert" className="text-sm text-destructive">{error}</p> : null}
      </CardContent>
    </Card>
  );
}
