import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useState } from "react";
import type { FlowPasskey } from "../../api/accountApi";

type PasskeySettingsSectionProps = {
  passkey: FlowPasskey;
  disabled: boolean;
  supported: boolean;
  onRegister: (displayName: string) => Promise<boolean>;
  onRemove: (credentialId: string) => Promise<void>;
  onRename: (credentialId: string, displayName: string) => Promise<boolean>;
};

function formatAddedDate(value: string | null) {
  if (!value) return null;
  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) return null;
  return new Date(parsed).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

export function PasskeySettingsSection({
  passkey,
  disabled,
  supported,
  onRegister,
  onRemove,
  onRename
}: PasskeySettingsSectionProps) {
  const [newName, setNewName] = useState("");
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const credentials = passkey.credentials ?? [];

  async function handleRegister() {
    const ok = await onRegister(newName.trim());
    if (ok) {
      setNewName("");
    }
  }

  async function handleSaveRename(credentialId: string) {
    const trimmed = editName.trim();
    if (!trimmed) return;
    const ok = await onRename(credentialId, trimmed);
    if (ok) {
      setEditingId(null);
      setEditName("");
    }
  }

  return (
    <section aria-label="Passkeys" className="space-y-4 border-t pt-4">
      <div className="space-y-1">
        <h3 className="text-sm font-semibold">Passkeys</h3>
        <p className="text-xs text-muted-foreground">
          Sign in without a password using this device or a security key. Register multiple passkeys so you have a backup.
        </p>
      </div>

      {credentials.length > 0 ? (
        <ul className="space-y-2">
          {credentials.map(credential => {
            const added = formatAddedDate(credential.addedAt);
            const editing = editingId === credential.id;
            return (
              <li key={credential.id} className="rounded-md border p-3 space-y-2">
                {editing ? (
                  <div className="space-y-2">
                    <Label htmlFor={`rename-passkey-${credential.id}`}>Edit passkey name</Label>
                    <Input
                      id={`rename-passkey-${credential.id}`}
                      value={editName}
                      onChange={event => setEditName(event.target.value)}
                      maxLength={120}
                      disabled={disabled}
                    />
                    <div className="flex gap-2">
                      <Button
                        type="button"
                        size="sm"
                        disabled={disabled || !editName.trim()}
                        onClick={() => { void handleSaveRename(credential.id); }}
                      >
                        Save name
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={disabled}
                        onClick={() => { setEditingId(null); setEditName(""); }}
                      >
                        Cancel
                      </Button>
                    </div>
                  </div>
                ) : (
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="text-sm font-medium break-words">{credential.displayName}</p>
                      {added ? <p className="text-xs text-muted-foreground">Added {added}</p> : null}
                      {!credential.canRemove ? (
                        <p className="text-xs text-muted-foreground">Keep at least one sign-in method on your account.</p>
                      ) : null}
                    </div>
                    <div className="flex shrink-0 gap-2">
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={disabled}
                        aria-label={`Rename ${credential.displayName}`}
                        onClick={() => { setEditingId(credential.id); setEditName(credential.displayName); }}
                      >
                        Rename
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={disabled || !credential.canRemove}
                        aria-label={`Remove ${credential.displayName}`}
                        onClick={() => { void onRemove(credential.id); }}
                      >
                        Remove
                      </Button>
                    </div>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      ) : (
        <p className="text-sm text-muted-foreground">No passkeys registered yet.</p>
      )}

      {passkey.registrationOptions ? (
        <div className="space-y-2">
          <Label htmlFor="passkey-display-name">Passkey name</Label>
          <Input
            id="passkey-display-name"
            type="text"
            value={newName}
            onChange={event => setNewName(event.target.value)}
            placeholder="e.g. Work laptop or security key"
            maxLength={120}
            disabled={disabled || !supported}
          />
          <Button
            type="button"
            variant="outline"
            className="w-full"
            disabled={disabled || !supported}
            onClick={() => { void handleRegister(); }}
          >
            Add passkey
          </Button>
          {!supported ? (
            <p className="text-xs text-muted-foreground">This browser or device does not support passkeys.</p>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
