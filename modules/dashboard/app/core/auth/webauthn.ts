export type PasskeyErrorCode =
  | "unsupported"
  | "cancelled"
  | "already_registered"
  | "invalid_options"
  | "failed";

export class PasskeyError extends Error {
  readonly code: PasskeyErrorCode;

  constructor(code: PasskeyErrorCode, message: string) {
    super(message);
    this.name = "PasskeyError";
    this.code = code;
  }
}

export function isPasskeySupported(): boolean {
  return typeof window !== "undefined" &&
    typeof window.PublicKeyCredential !== "undefined" &&
    typeof navigator !== "undefined" &&
    typeof navigator.credentials?.create === "function" &&
    typeof navigator.credentials?.get === "function";
}

export function base64UrlToUint8Array(value: string): Uint8Array<ArrayBuffer> {
  if (typeof value !== "string" || value.length === 0) {
    throw new PasskeyError("invalid_options", "Passkey challenge data is invalid.");
  }
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padding = normalized.length % 4 === 0 ? "" : "=".repeat(4 - (normalized.length % 4));
  let binary: string;
  try {
    binary = atob(normalized + padding);
  } catch {
    throw new PasskeyError("invalid_options", "Passkey challenge data is invalid.");
  }
  const buffer = new ArrayBuffer(binary.length);
  const bytes = new Uint8Array(buffer);
  for (let index = 0; index < binary.length; index++) {
    bytes[index] = binary.charCodeAt(index);
  }
  return bytes;
}

export function bufferToBase64Url(value: ArrayBuffer | Uint8Array): string {
  const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
  let binary = "";
  for (let index = 0; index < bytes.length; index++) {
    binary += String.fromCharCode(bytes[index]);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

type RawCredentialDescriptor = {
  type: PublicKeyCredentialType;
  id: string;
  transports?: AuthenticatorTransport[];
};

type RawCreationPayload = {
  publicKey?: {
    challenge?: string;
    rp?: PublicKeyCredentialRpEntity;
    user?: { id?: string; name?: string; displayName?: string };
    pubKeyCredParams?: PublicKeyCredentialParameters[];
    timeout?: number;
    excludeCredentials?: RawCredentialDescriptor[];
    authenticatorSelection?: AuthenticatorSelectionCriteria;
    attestation?: AttestationConveyancePreference;
    extensions?: AuthenticationExtensionsClientInputs;
  };
};

type RawRequestPayload = {
  publicKey?: {
    challenge?: string;
    timeout?: number;
    rpId?: string;
    allowCredentials?: RawCredentialDescriptor[];
    userVerification?: UserVerificationRequirement;
    extensions?: AuthenticationExtensionsClientInputs;
  };
};

function mapWebAuthnError(error: unknown, mode: "login" | "register"): PasskeyError {
  if (error instanceof PasskeyError) return error;
  const name = error && typeof error === "object" && "name" in error && typeof error.name === "string"
    ? error.name : "";
  if (name === "NotAllowedError" || name === "AbortError") {
    return new PasskeyError(
      "cancelled",
      mode === "login"
        ? "Passkey sign-in was cancelled. You can try again or use your password."
        : "Passkey registration was cancelled. Try again when you are ready."
    );
  }
  if (name === "InvalidStateError") {
    return new PasskeyError("already_registered", "This passkey is already registered on this account.");
  }
  if (name === "SecurityError") {
    return new PasskeyError("failed", "Passkeys require HTTPS on the configured dashboard domain.");
  }
  if (name === "NotSupportedError") {
    return new PasskeyError("unsupported", "This browser or device does not support passkeys.");
  }
  return new PasskeyError(
    "failed",
    mode === "login"
      ? "Passkey sign-in could not be completed. Try again or use your password."
      : "Passkey registration could not be completed. Try again."
  );
}

function decodeDescriptor(descriptor: RawCredentialDescriptor): PublicKeyCredentialDescriptor {
  return {
    type: descriptor.type ?? "public-key",
    id: base64UrlToUint8Array(descriptor.id),
    ...(descriptor.transports ? { transports: descriptor.transports } : {})
  };
}

/**
 * Runs WebAuthn credential creation for a Kratos passkey settings flow and returns the JSON payload
 * expected by `passkey_settings_register`.
 */
export async function createPasskeyCredential(registrationOptionsJson: string): Promise<string> {
  if (!isPasskeySupported()) {
    throw new PasskeyError("unsupported", "This browser or device does not support passkeys.");
  }
  let parsed: RawCreationPayload;
  try {
    parsed = JSON.parse(registrationOptionsJson) as RawCreationPayload;
  } catch {
    throw new PasskeyError("invalid_options", "Passkey registration options are invalid.");
  }
  const source = parsed?.publicKey;
  if (!source?.challenge || !source.rp?.id || !source.user?.id) {
    throw new PasskeyError("invalid_options", "Passkey registration options are incomplete.");
  }

  // Allow both built-in device passkeys and roaming hardware security keys to enroll as discoverable passkeys.
  const authenticatorSelection = source.authenticatorSelection
    ? { ...source.authenticatorSelection }
    : { requireResidentKey: true, residentKey: "required" as ResidentKeyRequirement, userVerification: "preferred" as UserVerificationRequirement };
  delete authenticatorSelection.authenticatorAttachment;

  const publicKey: PublicKeyCredentialCreationOptions = {
    challenge: base64UrlToUint8Array(source.challenge),
    rp: source.rp,
    user: {
      id: base64UrlToUint8Array(source.user.id),
      name: source.user.name ?? "",
      displayName: source.user.displayName ?? source.user.name ?? ""
    },
    pubKeyCredParams: source.pubKeyCredParams ?? [{ type: "public-key", alg: -7 }, { type: "public-key", alg: -257 }],
    ...(source.timeout !== undefined ? { timeout: source.timeout } : {}),
    ...(source.excludeCredentials ? { excludeCredentials: source.excludeCredentials.map(decodeDescriptor) } : {}),
    authenticatorSelection,
    ...(source.attestation ? { attestation: source.attestation } : {}),
    ...(source.extensions ? { extensions: source.extensions } : {})
  };

  try {
    const credential = await navigator.credentials.create({ publicKey }) as PublicKeyCredential | null;
    if (!credential || credential.type !== "public-key") {
      throw new PasskeyError("cancelled", "Passkey registration was cancelled. Try again when you are ready.");
    }
    const response = credential.response as AuthenticatorAttestationResponse;
    if (!response?.attestationObject || !response?.clientDataJSON) {
      throw new PasskeyError("failed", "Passkey registration could not be completed. Try again.");
    }
    return JSON.stringify({
      id: credential.id,
      rawId: bufferToBase64Url(credential.rawId),
      type: credential.type,
      response: {
        attestationObject: bufferToBase64Url(response.attestationObject),
        clientDataJSON: bufferToBase64Url(response.clientDataJSON)
      }
    });
  } catch (error) {
    throw mapWebAuthnError(error, "register");
  }
}

/**
 * Runs WebAuthn assertion for a Kratos passkey login (or re-authentication) flow and returns the JSON payload
 * expected by `passkey_login`.
 */
export async function authenticateWithPasskey(challengeOptionsJson: string): Promise<string> {
  if (!isPasskeySupported()) {
    throw new PasskeyError("unsupported", "This browser or device does not support passkeys.");
  }
  let parsed: RawRequestPayload;
  try {
    parsed = JSON.parse(challengeOptionsJson) as RawRequestPayload;
  } catch {
    throw new PasskeyError("invalid_options", "Passkey sign-in options are invalid.");
  }
  const source = parsed?.publicKey;
  if (!source?.challenge) {
    throw new PasskeyError("invalid_options", "Passkey sign-in options are incomplete.");
  }

  const publicKey: PublicKeyCredentialRequestOptions = {
    challenge: base64UrlToUint8Array(source.challenge),
    ...(source.timeout !== undefined ? { timeout: source.timeout } : {}),
    ...(source.rpId ? { rpId: source.rpId } : {}),
    ...(source.allowCredentials ? { allowCredentials: source.allowCredentials.map(decodeDescriptor) } : {}),
    ...(source.userVerification ? { userVerification: source.userVerification } : {}),
    ...(source.extensions ? { extensions: source.extensions } : {})
  };

  try {
    const credential = await navigator.credentials.get({ publicKey }) as PublicKeyCredential | null;
    if (!credential || credential.type !== "public-key") {
      throw new PasskeyError("cancelled", "Passkey sign-in was cancelled. You can try again or use your password.");
    }
    const response = credential.response as AuthenticatorAssertionResponse;
    if (!response?.authenticatorData || !response?.clientDataJSON || !response?.signature) {
      throw new PasskeyError("failed", "Passkey sign-in could not be completed. Try again or use your password.");
    }
    const userHandle = response.userHandle && response.userHandle.byteLength > 0
      ? bufferToBase64Url(response.userHandle)
      : "";
    return JSON.stringify({
      id: credential.id,
      rawId: bufferToBase64Url(credential.rawId),
      type: credential.type,
      response: {
        authenticatorData: bufferToBase64Url(response.authenticatorData),
        clientDataJSON: bufferToBase64Url(response.clientDataJSON),
        signature: bufferToBase64Url(response.signature),
        userHandle
      }
    });
  } catch (error) {
    throw mapWebAuthnError(error, "login");
  }
}
