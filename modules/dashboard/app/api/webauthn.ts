export type PasskeyDescriptorJson = {
  type: "public-key";
  id: string;
  transports?: AuthenticatorTransport[];
};

export type PasskeyLoginOptionsJson = {
  publicKey: {
    challenge: string;
    rpId?: string;
    timeout?: number;
    userVerification?: UserVerificationRequirement;
    allowCredentials?: PasskeyDescriptorJson[];
  };
};

export type PasskeyRegistrationOptionsJson = {
  publicKey: {
    challenge: string;
    rp: { name: string; id?: string };
    user: { id: string; name: string; displayName: string };
    pubKeyCredParams: Array<{ type: "public-key"; alg: number }>;
    timeout?: number;
    attestation?: AttestationConveyancePreference;
    authenticatorSelection?: AuthenticatorSelectionCriteria;
    excludeCredentials?: PasskeyDescriptorJson[];
  };
};

export function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/");
  const padding = normalized.length % 4 === 0 ? "" : "=".repeat(4 - (normalized.length % 4));
  const binary = atob(normalized + padding);
  const buffer = new ArrayBuffer(binary.length);
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

export function bytesToBase64Url(input: ArrayBuffer | ArrayBufferView): string {
  const bytes = input instanceof ArrayBuffer
    ? new Uint8Array(input)
    : new Uint8Array(input.buffer, input.byteOffset, input.byteLength);
  let binary = "";
  for (let i = 0; i < bytes.length; i++) {
    binary += String.fromCharCode(bytes[i]);
  }
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

export function isPasskeySupported(): boolean {
  return typeof window !== "undefined" &&
    window.isSecureContext !== false &&
    typeof window.PublicKeyCredential !== "undefined" &&
    typeof navigator !== "undefined" &&
    typeof navigator.credentials?.create === "function" &&
    typeof navigator.credentials?.get === "function";
}

function toDescriptors(items: PasskeyDescriptorJson[] | undefined): PublicKeyCredentialDescriptor[] | undefined {
  if (!items) return undefined;
  return items.map(item => ({
    type: "public-key",
    id: base64UrlToBytes(item.id),
    ...(item.transports && item.transports.length > 0 ? { transports: item.transports } : {})
  }));
}

export async function createPasskeyCredential(options: PasskeyRegistrationOptionsJson): Promise<string> {
  if (!isPasskeySupported()) {
    throw new DOMException("Passkeys are not supported in this browser.", "NotSupportedError");
  }
  const pk = options.publicKey;
  const publicKey: PublicKeyCredentialCreationOptions = {
    challenge: base64UrlToBytes(pk.challenge),
    rp: { name: pk.rp.name, ...(pk.rp.id ? { id: pk.rp.id } : {}) },
    user: {
      id: base64UrlToBytes(pk.user.id),
      name: pk.user.name,
      displayName: pk.user.displayName
    },
    pubKeyCredParams: pk.pubKeyCredParams.map(param => ({ type: "public-key", alg: param.alg })),
    ...(pk.timeout !== undefined ? { timeout: pk.timeout } : {}),
    ...(pk.attestation !== undefined ? { attestation: pk.attestation } : {}),
    ...(pk.authenticatorSelection !== undefined ? { authenticatorSelection: pk.authenticatorSelection } : {}),
    ...(pk.excludeCredentials !== undefined ? { excludeCredentials: toDescriptors(pk.excludeCredentials) } : {})
  };

  const credential = await navigator.credentials.create({ publicKey });
  if (!credential || credential.type !== "public-key") {
    throw new DOMException("No passkey credential was created.", "NotAllowedError");
  }
  const publicKeyCredential = credential as PublicKeyCredential;
  const response = publicKeyCredential.response as AuthenticatorAttestationResponse;
  if (!response?.attestationObject || !response?.clientDataJSON) {
    throw new Error("Unexpected WebAuthn attestation response.");
  }
  const transports = typeof response.getTransports === "function" ? response.getTransports() : [];
  const rawId = bytesToBase64Url(publicKeyCredential.rawId);
  const payload: Record<string, unknown> = {
    id: publicKeyCredential.id || rawId,
    rawId,
    type: "public-key",
    response: {
      attestationObject: bytesToBase64Url(response.attestationObject),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      ...(transports.length > 0 ? { transports } : {})
    }
  };
  if (publicKeyCredential.authenticatorAttachment === "platform" ||
      publicKeyCredential.authenticatorAttachment === "cross-platform") {
    payload.authenticatorAttachment = publicKeyCredential.authenticatorAttachment;
  }
  return JSON.stringify(payload);
}

export async function getPasskeyAssertion(options: PasskeyLoginOptionsJson): Promise<string> {
  if (!isPasskeySupported()) {
    throw new DOMException("Passkeys are not supported in this browser.", "NotSupportedError");
  }
  const pk = options.publicKey;
  const publicKey: PublicKeyCredentialRequestOptions = {
    challenge: base64UrlToBytes(pk.challenge),
    ...(pk.rpId ? { rpId: pk.rpId } : {}),
    ...(pk.timeout !== undefined ? { timeout: pk.timeout } : {}),
    ...(pk.userVerification !== undefined ? { userVerification: pk.userVerification } : {}),
    ...(pk.allowCredentials !== undefined ? { allowCredentials: toDescriptors(pk.allowCredentials) } : {})
  };

  const credential = await navigator.credentials.get({ publicKey });
  if (!credential || credential.type !== "public-key") {
    throw new DOMException("No passkey credential was selected.", "NotAllowedError");
  }
  const publicKeyCredential = credential as PublicKeyCredential;
  const response = publicKeyCredential.response as AuthenticatorAssertionResponse;
  if (!response?.authenticatorData || !response?.clientDataJSON || !response?.signature) {
    throw new Error("Unexpected WebAuthn assertion response.");
  }
  const rawId = bytesToBase64Url(publicKeyCredential.rawId);
  return JSON.stringify({
    id: publicKeyCredential.id || rawId,
    rawId,
    type: "public-key",
    response: {
      authenticatorData: bytesToBase64Url(response.authenticatorData),
      clientDataJSON: bytesToBase64Url(response.clientDataJSON),
      signature: bytesToBase64Url(response.signature),
      userHandle: response.userHandle && response.userHandle.byteLength > 0
        ? bytesToBase64Url(response.userHandle)
        : null
    }
  });
}

export function classifyPasskeyError(error: unknown, mode: "login" | "register"): { cancelled: boolean; message: string } {
  const name = error instanceof Error ? error.name : typeof error === "object" && error !== null && "name" in error
    ? String((error as { name: unknown }).name) : "";
  if (name === "NotAllowedError" || name === "AbortError") {
    return {
      cancelled: true,
      message: mode === "register"
        ? "Passkey setup was cancelled. You can try again whenever you are ready."
        : "Passkey sign-in was cancelled. Try again or sign in with your password."
    };
  }
  if (name === "InvalidStateError") {
    return {
      cancelled: false,
      message: "This authenticator already has a passkey for this account."
    };
  }
  if (name === "SecurityError") {
    return {
      cancelled: false,
      message: "Passkeys require HTTPS on the configured dashboard hostname."
    };
  }
  if (name === "NotSupportedError") {
    return {
      cancelled: false,
      message: "This browser or device does not support passkeys."
    };
  }
  return {
    cancelled: false,
    message: mode === "register"
      ? "Could not register a passkey on this device. Try again or use another authenticator."
      : "Could not complete passkey sign-in. Try again or sign in with your password."
  };
}
