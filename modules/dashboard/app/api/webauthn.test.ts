import { afterEach, describe, expect, it, vi } from "vitest";
import {
  base64UrlToBytes, bytesToBase64Url, classifyPasskeyError, createPasskeyCredential, getPasskeyAssertion, isPasskeySupported
} from "./webauthn";

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("webauthn passkey helpers", () => {
  it("round-trips base64url encoding without padding", () => {
    const raw = new Uint8Array([0, 1, 2, 251, 252, 253, 254, 255]);
    const encoded = bytesToBase64Url(raw);
    expect(encoded).not.toMatch(/[+/=]/);
    expect(Array.from(base64UrlToBytes(encoded))).toEqual(Array.from(raw));
  });

  it("serializes a WebAuthn creation credential for Kratos passkey registration", async () => {
    vi.stubGlobal("PublicKeyCredential", function PublicKeyCredential() {});
    const create = vi.fn().mockResolvedValue({
      id: "Y3JlZC0x",
      rawId: base64UrlToBytes("Y3JlZC0x").buffer,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        attestationObject: base64UrlToBytes("YXR0ZXN0").buffer,
        clientDataJSON: base64UrlToBytes("Y2xpZW50").buffer,
        getTransports: () => ["internal", "hybrid"]
      }
    });
    vi.stubGlobal("navigator", { credentials: { create, get: vi.fn() } });
    expect(isPasskeySupported()).toBe(true);

    const serialized = await createPasskeyCredential({
      publicKey: {
        challenge: "Y2hhbGxlbmdl",
        rp: { name: "Minicloud", id: "console.example.test" },
        user: { id: "dXNlci0x", name: "operator@example.test", displayName: "operator@example.test" },
        pubKeyCredParams: [{ type: "public-key", alg: -7 }],
        excludeCredentials: [{ type: "public-key", id: "b2xkLWNyZWQ", transports: ["internal"] }]
      }
    });

    const callArg = create.mock.calls[0][0].publicKey as PublicKeyCredentialCreationOptions;
    expect(callArg.challenge).toBeInstanceOf(Uint8Array);
    expect(callArg.user.id).toBeInstanceOf(Uint8Array);
    expect(callArg.excludeCredentials?.[0].id).toBeInstanceOf(Uint8Array);
    expect(JSON.parse(serialized)).toEqual({
      id: "Y3JlZC0x",
      rawId: "Y3JlZC0x",
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        attestationObject: "YXR0ZXN0",
        clientDataJSON: "Y2xpZW50",
        transports: ["internal", "hybrid"]
      }
    });
  });

  it("serializes a WebAuthn assertion credential for Kratos passkey login", async () => {
    vi.stubGlobal("PublicKeyCredential", function PublicKeyCredential() {});
    const get = vi.fn().mockResolvedValue({
      id: "Y3JlZC0x",
      rawId: base64UrlToBytes("Y3JlZC0x").buffer,
      type: "public-key",
      response: {
        authenticatorData: base64UrlToBytes("YXV0aA").buffer,
        clientDataJSON: base64UrlToBytes("Y2xpZW50").buffer,
        signature: base64UrlToBytes("c2ln").buffer,
        userHandle: base64UrlToBytes("dXNlcg").buffer
      }
    });
    vi.stubGlobal("navigator", { credentials: { create: vi.fn(), get } });

    const serialized = await getPasskeyAssertion({
      publicKey: {
        challenge: "Y2hhbGxlbmdl",
        rpId: "console.example.test",
        allowCredentials: [{ type: "public-key", id: "Y3JlZC0x" }]
      }
    });

    const callArg = get.mock.calls[0][0].publicKey as PublicKeyCredentialRequestOptions;
    expect(callArg.challenge).toBeInstanceOf(Uint8Array);
    expect(callArg.allowCredentials?.[0].id).toBeInstanceOf(Uint8Array);
    expect(JSON.parse(serialized)).toEqual({
      id: "Y3JlZC0x",
      rawId: "Y3JlZC0x",
      type: "public-key",
      response: {
        authenticatorData: "YXV0aA",
        clientDataJSON: "Y2xpZW50",
        signature: "c2ln",
        userHandle: "dXNlcg"
      }
    });
  });

  it("classifies browser WebAuthn cancellation and environment errors cleanly", () => {
    expect(classifyPasskeyError(new DOMException("Cancelled", "NotAllowedError"), "login")).toEqual({
      cancelled: true,
      message: "Passkey sign-in was cancelled. Try again or sign in with your password."
    });
    expect(classifyPasskeyError(new DOMException("Cancelled", "AbortError"), "register")).toEqual({
      cancelled: true,
      message: "Passkey setup was cancelled. You can try again whenever you are ready."
    });
    expect(classifyPasskeyError(new DOMException("Duplicate", "InvalidStateError"), "register")).toEqual({
      cancelled: false,
      message: "This authenticator already has a passkey for this account."
    });
    expect(classifyPasskeyError(new DOMException("Insecure", "SecurityError"), "login")).toEqual({
      cancelled: false,
      message: "Passkeys require HTTPS on the configured dashboard hostname."
    });
  });
});
