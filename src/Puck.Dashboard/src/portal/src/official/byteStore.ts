/**
 * A key-value byte store for verified official content: the Cache Storage API in a browser,
 * an in-memory Map anywhere `caches` is not global (Node's test runner). Bytes only land here
 * after `verify.ts` has checked them, and an object read back is checked again
 * (`readThroughVerified`), because the store is the page's to write and a copy can change after it
 * was stored.
 *
 * Keys are content hashes ("sha256/<hex64>") for official objects, and a fixed
 * "manifest/<channel>" key for the last network-verified manifest text, used for the offline
 * fallback in officialClient.ts.
 */
import { OfficialRefusal, verifyBytes } from "./verify";

export interface ByteStore {
  /** Whether the store holds `key`, without reading its bytes. */
  has(key: string): Promise<boolean>;
  get(key: string): Promise<Uint8Array | undefined>;
  put(key: string, bytes: Uint8Array, contentType: string): Promise<void>;
}

const CACHE_NAME = "puck-official-objects-v1";
// Cache Storage keys objects by Request/URL, not by an arbitrary string — this fixed, invalid
// origin turns any store key into a same-shape Request key without ever being dereferenced.
const CACHE_KEY_ORIGIN = "https://puck.official.cache.invalid/";

class MemoryByteStore implements ByteStore {
  private readonly entries = new Map<string, Uint8Array>();

  async has(key: string): Promise<boolean> {
    return this.entries.has(key);
  }

  async get(key: string): Promise<Uint8Array | undefined> {
    return this.entries.get(key);
  }

  async put(key: string, bytes: Uint8Array): Promise<void> {
    this.entries.set(key, bytes);
  }
}

class CacheStorageByteStore implements ByteStore {
  private opening: Promise<Cache> | null = null;

  private open(): Promise<Cache> {
    this.opening ??= caches.open(CACHE_NAME);
    return this.opening;
  }

  private static keyUrl(key: string): string {
    return new URL(encodeURIComponent(key), CACHE_KEY_ORIGIN).href;
  }

  async has(key: string): Promise<boolean> {
    const cache = await this.open();
    return (await cache.match(CacheStorageByteStore.keyUrl(key))) !== undefined;
  }

  async get(key: string): Promise<Uint8Array | undefined> {
    const cache = await this.open();
    const response = await cache.match(CacheStorageByteStore.keyUrl(key));
    if (!response) {
      return undefined;
    }
    return new Uint8Array(await response.arrayBuffer());
  }

  async put(key: string, bytes: Uint8Array, contentType: string): Promise<void> {
    const cache = await this.open();
    // Uint8Array itself satisfies BodyInit at runtime, but this lib's DOM types don't say so —
    // hand Response the plain ArrayBuffer copy instead of fighting that typing gap.
    const body = bytes.slice().buffer;
    await cache.put(
      CacheStorageByteStore.keyUrl(key),
      new Response(body, { headers: { "content-type": contentType } }),
    );
  }
}

export function createByteStore(): ByteStore {
  return typeof caches !== "undefined" ? new CacheStorageByteStore() : new MemoryByteStore();
}

export function manifestCacheKey(channel: string): string {
  return `manifest/${channel}`;
}

/**
 * One official object's verified bytes, keyed by its manifest hash. A stored copy answers only when it still matches
 * the hash. A tampered copy is treated as absent: the object is fetched again, verified, and the good bytes replace
 * the bad ones. Fetched bytes are verified before anything is stored, and a mismatch stores nothing. Every refusal
 * names the object: a fetched mismatch by `verifyBytes`, and a refetch that fails after a tampered copy by both
 * reasons.
 */
export async function readThroughVerified(
  store: ByteStore,
  objectPath: string,
  expectedHash: string,
  contentType: string,
  fetchBytes: () => Promise<Uint8Array>,
): Promise<Uint8Array> {
  const stored = await store.get(expectedHash);
  let tampered: OfficialRefusal | null = null;
  if (stored) {
    try {
      return await verifyBytes(objectPath, stored, expectedHash);
    } catch (error) {
      if (!(error instanceof OfficialRefusal)) throw error;
      tampered = error;
    }
  }
  let bytes: Uint8Array;
  try {
    bytes = await fetchBytes();
  } catch (error) {
    if (!tampered) throw error;
    throw new OfficialRefusal(
      `${tampered.message} Fetching '${objectPath}' again to replace it failed: ${error instanceof Error ? error.message : String(error)}`,
      { objectPath, expectedHash, actualHash: tampered.actualHash },
    );
  }
  await verifyBytes(objectPath, bytes, expectedHash);
  await store.put(expectedHash, bytes, contentType);
  return bytes;
}
