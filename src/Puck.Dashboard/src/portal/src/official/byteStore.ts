/**
 * A key-value byte store for verified official content: the Cache Storage API in a browser,
 * an in-memory Map anywhere `caches` is not global (Node's test runner). Every store lookup
 * re-verifies nothing — bytes only ever land here after `verify.ts` has already checked them —
 * but a store miss means the caller must fetch and verify before calling `put`.
 *
 * Keys are content hashes ("sha256/<hex64>") for official objects, and a fixed
 * "manifest/<channel>" key for the last network-verified manifest text, used for the offline
 * fallback in officialClient.ts.
 */

export interface ByteStore {
  get(key: string): Promise<Uint8Array | undefined>;
  put(key: string, bytes: Uint8Array, contentType: string): Promise<void>;
}

const CACHE_NAME = "puck-official-objects-v1";
// Cache Storage keys objects by Request/URL, not by an arbitrary string — this fixed, invalid
// origin turns any store key into a same-shape Request key without ever being dereferenced.
const CACHE_KEY_ORIGIN = "https://puck.official.cache.invalid/";

class MemoryByteStore implements ByteStore {
  private readonly entries = new Map<string, Uint8Array>();

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
