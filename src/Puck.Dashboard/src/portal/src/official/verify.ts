/**
 * Content-address verification for official objects: every "hash" in the manifest is
 * "sha256/<hex64 lowercase>" over the exact bytes at "path". `crypto.subtle` runs the digest —
 * a real browser `SubtleCrypto` in the studio, Node's own `webcrypto` global under the test
 * runner (both expose `globalThis.crypto.subtle`, no import needed).
 */

export class OfficialRefusal extends Error {
  readonly objectPath?: string;
  readonly expectedHash?: string;
  readonly actualHash?: string;

  constructor(message: string, details: { objectPath?: string; expectedHash?: string; actualHash?: string } = {}) {
    super(message);
    this.name = "OfficialRefusal";
    this.objectPath = details.objectPath;
    this.expectedHash = details.expectedHash;
    this.actualHash = details.actualHash;
  }

  static hashMismatch(objectPath: string, expectedHash: string, actualHash: string): OfficialRefusal {
    return new OfficialRefusal(
      `official object '${objectPath}' failed verification: expected ${expectedHash}, got ${actualHash}.`,
      { objectPath, expectedHash, actualHash },
    );
  }
}

function subtle(): SubtleCrypto {
  const value = (globalThis.crypto as Crypto | undefined)?.subtle;
  if (!value) {
    throw new Error("crypto.subtle is not available in this runtime.");
  }
  return value;
}

export async function sha256Hex(bytes: Uint8Array): Promise<string> {
  // Uint8Array is a BufferSource; subtle.digest hashes exactly its byteOffset..byteLength
  // window, so a view over a larger buffer needs no copy here.
  const digest = await subtle().digest("SHA-256", bytes as BufferSource);
  return Array.from(new Uint8Array(digest))
    .map((byte) => byte.toString(16).padStart(2, "0"))
    .join("");
}

export function toHashName(hex: string): string {
  return `sha256/${hex}`;
}

/**
 * Hashes `bytes` and refuses by name on a mismatch against `expectedHash`. Never caches — the
 * caller stores bytes only after this resolves, so a refusal here leaves the byte store
 * untouched.
 */
export async function verifyBytes(objectPath: string, bytes: Uint8Array, expectedHash: string): Promise<Uint8Array> {
  const actualHash = toHashName(await sha256Hex(bytes));
  if (actualHash !== expectedHash) {
    throw OfficialRefusal.hashMismatch(objectPath, expectedHash, actualHash);
  }
  return bytes;
}
