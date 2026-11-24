import { Button } from "@/components/ui/button";
import { useState } from "react";
import type { FlowPasskey } from "../../api/accountApi";

function formatAddedDate(addedAt: string | null): string | null {
  if (!addedAt) return null;
  const parsed = Date.parse(addedAt);
  if (Number.isNaN(parsed)) return null;
  return new Date(parsed).toLocaleDateString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric"
  });
}

export function PasskeySettingsSection({
  passkey,
  browserSupported,
  busy,
  disabled,
  onAdd,
  onRemove
}: {
  passkey: FlowPasskey;
  browserSupported: boolean;
  busy: boolean;
  disabled: boolean;
  onAdd: () => void;
  onRemove: (credentialId: string) => void;
}) {
  const [confirmRemoveId, setConfirmRemoveId] = useState<string | null>(null);
  const count = passkey.credentials.length;
  const statusText = count === 0
    ? "No passkeys enrolled."
    : count === 1
      ? "1 passkey enrolled."
      : `${count} passkeys enrolled.`;

  return (
    <section aria-labelledby="passkeys-heading" className="space-y-3 border-t pt-4">
      <div className="space-y-1">
        <div className="flex items-center justify-between gap-2">
          <h3 id="passkeys-heading" className="text-sm font-medium">Passkeys</h3>
          <span className="text-xs text-muted-foreground">{statusText}</span>
        </div>
        <p className="text-xs text-muted-foreground">
          {count === 0
            ? "Add a passkey to sign in without typing your password. Your password remains available as a fallback."
            : count === 1
              ? "Add a second passkey on another device or authenticator as a backup. Your password remains available as a fallback."
              : "You can sign in with any enrolled passkey or your password."}
        </p>
      </div>

      {count > 0 ? (
        <ul className="space-y-2" aria-label="Enrolled passkeys">
          {passkey.credentials.map(credential => {
            const added = formatAddedDate(credential.addedAt);
            const confirming = confirmRemoveId === credential.id;
            return (
              <li key={credential.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border px-3 py-2 text-sm">
                <div className="min-w-0">
                  <div className="truncate font-medium">{credential.displayName}</div>
                  <div className="text-xs text-muted-foreground">
                    {added ? `Added ${added}` : "Enrolled passkey"}
                    {!credential.canRemove ? " · Keep at least one sign-in method" : ""}
                  </div>
                </div>
                <div className="flex items-center gap-2">
                  {confirming ? (
                    <>
                      <Button
                        type="button"
                        size="sm"
                        variant="destructive"
                        disabled={busy || disabled || !credential.canRemove}
                        onClick={() => {
                          setConfirmRemoveId(null);
                          onRemove(credential.id);
                        }}
                      >
                        Confirm remove
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={busy}
                        onClick={() => setConfirmRemoveId(null)}
                      >
                        Cancel
                      </Button>
                    </>
                  ) : (
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={busy || disabled || !credential.canRemove}
                      onClick={() => setConfirmRemoveId(credential.id)}
                    >
                      Remove
                    </Button>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      ) : null}

      {passkey.registrationOptions ? (
        <div className="space-y-2">
          <Button
            type="button"
            variant="outline"
            className="w-full"
            disabled={busy || disabled || !browserSupported}
            onClick={() => {
              setConfirmRemoveId(null);
              onAdd();
            }}
          >
            {count === 0 ? "Add passkey" : "Add another passkey"}
          </Button>
          {!browserSupported ? (
            <p className="text-xs text-muted-foreground">
              Passkeys require a WebAuthn-capable browser over HTTPS.
            </p>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
