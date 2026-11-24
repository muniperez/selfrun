import { afterEach, describe, expect, it, vi } from "vitest";
import {
  authenticateWithPasskey,
  base64UrlToUint8Array,
  bufferToBase64Url,
  createPasskeyCredential,
  isPasskeySupported,
  PasskeyError
} from "./webauthn";

const originalPublicKeyCredential = window.PublicKeyCredential;
const originalCredentials = navigator.credentials;

function stubWebAuthn(credentials?: Partial<CredentialsContainer>) {
  Object.defineProperty(window, "PublicKeyCredential", {
    configurable: true,
    value: function PublicKeyCredential() {}
  });
  Object.defineProperty(navigator, "credentials", {
    configurable: true,
    value: {
      create: vi.fn(),
      get: vi.fn(),
      ...credentials
    }
  });
}

afterEach(() => {
  Object.defineProperty(window, "PublicKeyCredential", {
    configurable: true,
    value: originalPublicKeyCredential
  });
  Object.defineProperty(navigator, "credentials", {
    configurable: true,
    value: originalCredentials
  });
  vi.restoreAllMocks();
});

describe("webauthn helpers", () => {
  it("encodes and decodes base64url bytes symmetrically", () => {
    const sample = new Uint8Array([0, 1, 2, 3, 250, 251, 252, 253, 254, 255]);
    const encoded = bufferToBase64Url(sample);
    expect(encoded).not.toMatch(/[+/=]/);
    expect(Array.from(base64UrlToUint8Array(encoded))).toEqual(Array.from(sample));
    expect(() => base64UrlToUint8Array("")).toThrow(PasskeyError);
    expect(() => base64UrlToUint8Array("%%%invalid%%%")).toThrow(PasskeyError);
  });

  it("reports passkey support accurately", () => {
    Object.defineProperty(window, "PublicKeyCredential", { configurable: true, value: undefined });
    expect(isPasskeySupported()).toBe(false);
    stubWebAuthn();
    expect(isPasskeySupported()).toBe(true);
  });

  it("creates a discoverable passkey, strips authenticatorAttachment, and serializes base64url attestation", async () => {
    const create = vi.fn().mockResolvedValue({
      id: "AQIDBA",
      rawId: new Uint8Array([1, 2, 3, 4]).buffer,
      type: "public-key",
      response: {
        attestationObject: new Uint8Array([10, 11, 12]).buffer,
        clientDataJSON: new Uint8Array([20, 21, 22]).buffer
      }
    });
    stubWebAuthn({ create });

    const options = JSON.stringify({
      publicKey: {
        challenge: "c3ludGhldGljLWNoYWxsZW5nZQ",
        rp: { id: "dashboard.example.test", name: "Minicloud" },
        user: { id: "dXNlci0x", name: "operator@example.test", displayName: "Operator" },
        excludeCredentials: [{ type: "public-key", id: "AQIDBA", transports: ["internal"] }],
        authenticatorSelection: {
          authenticatorAttachment: "platform",
          residentKey: "required",
          userVerification: "preferred"
        }
      }
    });

    const serialized = await createPasskeyCredential(options);
    expect(create).toHaveBeenCalledOnce();
    const publicKeyArg = create.mock.calls[0][0].publicKey as PublicKeyCredentialCreationOptions;
    expect(publicKeyArg.authenticatorSelection?.authenticatorAttachment).toBeUndefined();
    expect(publicKeyArg.authenticatorSelection?.residentKey).toBe("required");
    expect(Array.from(new Uint8Array(publicKeyArg.excludeCredentials![0].id as ArrayBuffer))).toEqual([1, 2, 3, 4]);

    expect(JSON.parse(serialized)).toEqual({
      id: "AQIDBA",
      rawId: "AQIDBA",
      type: "public-key",
      response: {
        attestationObject: "CgsM",
        clientDataJSON: "FBUW"
      }
    });
  });

  it("maps browser registration errors to safe PasskeyError codes", async () => {
    const create = vi.fn();
    stubWebAuthn({ create });
    const valid = JSON.stringify({
      publicKey: {
        challenge: "c3ludGhldGlj",
        rp: { id: "dashboard.example.test", name: "Minicloud" },
        user: { id: "dXNlci0x", name: "operator@example.test", displayName: "Operator" }
      }
    });

    create.mockRejectedValueOnce(Object.assign(new Error("user cancelled"), { name: "NotAllowedError" }));
    await expect(createPasskeyCredential(valid)).rejects.toMatchObject({ code: "cancelled" });

    create.mockRejectedValueOnce(Object.assign(new Error("duplicate"), { name: "InvalidStateError" }));
    await expect(createPasskeyCredential(valid)).rejects.toMatchObject({ code: "already_registered" });

    create.mockRejectedValueOnce(Object.assign(new Error("insecure"), { name: "SecurityError" }));
    await expect(createPasskeyCredential(valid)).rejects.toMatchObject({
      code: "failed",
      message: expect.stringContaining("HTTPS")
    });

    await expect(createPasskeyCredential("{not-json")).rejects.toMatchObject({ code: "invalid_options" });
    await expect(createPasskeyCredential(JSON.stringify({ publicKey: {} }))).rejects.toMatchObject({ code: "invalid_options" });
  });

  it("authenticates with a passkey and serializes assertion including userHandle", async () => {
    const get = vi.fn().mockResolvedValue({
      id: "AQIDBA",
      rawId: new Uint8Array([1, 2, 3, 4]).buffer,
      type: "public-key",
      response: {
        authenticatorData: new Uint8Array([5, 6]).buffer,
        clientDataJSON: new Uint8Array([7, 8]).buffer,
        signature: new Uint8Array([9, 10]).buffer,
        userHandle: new Uint8Array([11, 12]).buffer
      }
    });
    stubWebAuthn({ get });

    const serialized = await authenticateWithPasskey(JSON.stringify({
      publicKey: {
        challenge: "c3ludGhldGljLWNoYWxsZW5nZQ",
        rpId: "dashboard.example.test",
        allowCredentials: [{ type: "public-key", id: "AQIDBA" }],
        userVerification: "preferred"
      }
    }));
    expect(get).toHaveBeenCalledOnce();
    expect(JSON.parse(serialized)).toEqual({
      id: "AQIDBA",
      rawId: "AQIDBA",
      type: "public-key",
      response: {
        authenticatorData: "BQY",
        clientDataJSON: "Bwg",
        signature: "CQo",
        userHandle: "Cww"
      }
    });
  });

  it("handles cancelled, null, unsupported, and malformed passkey authentication", async () => {
    const get = vi.fn();
    stubWebAuthn({ get });
    const valid = JSON.stringify({ publicKey: { challenge: "c3ludGhldGlj" } });

    get.mockResolvedValueOnce(null);
    await expect(authenticateWithPasskey(valid)).rejects.toMatchObject({ code: "cancelled" });

    get.mockRejectedValueOnce(Object.assign(new Error("aborted"), { name: "AbortError" }));
    await expect(authenticateWithPasskey(valid)).rejects.toMatchObject({ code: "cancelled" });

    await expect(authenticateWithPasskey("{}")).rejects.toMatchObject({ code: "invalid_options" });

    Object.defineProperty(window, "PublicKeyCredential", { configurable: true, value: undefined });
    await expect(authenticateWithPasskey(valid)).rejects.toMatchObject({ code: "unsupported" });
  });
});
