/**
 * Loads and verifies the official manifest, then exposes lazy, hash-verified access to every
 * object it names. Nothing here fetches until asked: `documents`/`composed`/`assets` resolve one
 * name to its verified text on first `get`, cached for the life of the returned `OfficialLoad`.
 */
import { type ByteStore, createByteStore, manifestCacheKey } from "./byteStore";
import {
  type ManifestAssetEntry,
  type ManifestBuild,
  type ManifestComposedEntry,
  type ManifestDocumentEntry,
  type ManifestEngineFile,
  type ManifestFileEntry,
  type OfficialManifest,
  parseManifest,
} from "./manifest";
import { type ResolvedOfficial } from "./officialBase";
import { OfficialRefusal, verifyBytes } from "./verify";

export type FetchLike = typeof fetch;

export interface OfficialFileRef {
  readonly url: string;
  readonly hash: string;
}

/** Lazily resolves one named entry (a document, a composed world, or an asset) to verified text. */
export interface LazyOfficialSet {
  names(): readonly string[];
  get(name: string): Promise<string>;
}

export type OfficialSource = "network" | "offline-cache";

export interface OfficialLoad {
  readonly manifest: OfficialManifest;
  readonly build: ManifestBuild;
  readonly schemaBundle: unknown;
  readonly documents: LazyOfficialSet;
  readonly composed: LazyOfficialSet;
  readonly assets: LazyOfficialSet;
  readonly engineEntryUrl: string;
  /** Every engine file keyed by its manifest `name` (e.g. "_framework/dotnet.js"), for a resourceLoader. */
  readonly engineFiles: Readonly<Record<string, OfficialFileRef>>;
  readonly source: OfficialSource;
}

async function fetchBytes(fetchImpl: FetchLike, url: string): Promise<Uint8Array> {
  const response = await fetchImpl(url);
  if (!response.ok) {
    throw new OfficialRefusal(`fetching official object '${url}' failed: HTTP ${response.status}.`);
  }
  return new Uint8Array(await response.arrayBuffer());
}

/** Fetches (or serves from the byte store) `entry`'s bytes, verifying on every network fetch. */
function fetchVerifiedBytes(
  official: ResolvedOfficial,
  fetchImpl: FetchLike,
  byteStore: ByteStore,
  entry: ManifestFileEntry,
): Promise<Uint8Array> {
  return (async () => {
    const stored = await byteStore.get(entry.hash);
    if (stored) {
      return stored;
    }
    const url = official.objectUrl(entry.path).href;
    const bytes = await fetchBytes(fetchImpl, url);
    // A mismatch throws here, before the store is ever touched — caches nothing on refusal.
    await verifyBytes(entry.path, bytes, entry.hash);
    await byteStore.put(entry.hash, bytes, entry.contentType);
    return bytes;
  })();
}

function buildLazySet<T extends ManifestFileEntry & { name: string }>(
  official: ResolvedOfficial,
  fetchImpl: FetchLike,
  byteStore: ByteStore,
  entries: readonly T[],
  keyOf: (entry: T) => string = (entry) => entry.name,
): LazyOfficialSet {
  const byKey = new Map<string, T>(entries.map((entry) => [keyOf(entry), entry]));
  const pending = new Map<string, Promise<string>>();

  return {
    names(): readonly string[] {
      return [...byKey.keys()];
    },
    get(name: string): Promise<string> {
      const entry = byKey.get(name);
      if (!entry) {
        throw new Error(`official manifest names no entry '${name}'.`);
      }
      let promise = pending.get(name);
      if (!promise) {
        promise = fetchVerifiedBytes(official, fetchImpl, byteStore, entry).then((bytes) =>
          new TextDecoder().decode(bytes),
        );
        pending.set(name, promise);
      }
      return promise;
    },
  };
}

async function loadManifestText(
  official: ResolvedOfficial,
  fetchImpl: FetchLike,
  byteStore: ByteStore,
): Promise<{ text: string; source: OfficialSource }> {
  const cacheKey = manifestCacheKey(official.channel);

  try {
    const response = await fetchImpl(official.manifestUrl.href);
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    const text = await response.text();
    await byteStore.put(cacheKey, new TextEncoder().encode(text), "application/json");
    return { text, source: "network" };
  } catch (networkError) {
    const cached = await byteStore.get(cacheKey);
    if (!cached) {
      throw new OfficialRefusal(
        `official manifest for channel '${official.channel}' is unreachable at ${official.manifestUrl.href} ` +
          `and no previously verified copy is cached: ${(networkError as Error).message}`,
      );
    }
    return { text: new TextDecoder().decode(cached), source: "offline-cache" };
  }
}

export async function loadOfficial(
  official: ResolvedOfficial,
  fetchImpl: FetchLike = fetch,
  byteStore: ByteStore = createByteStore(),
): Promise<OfficialLoad> {
  const { text, source } = await loadManifestText(official, fetchImpl, byteStore);
  const manifest = parseManifest(text);

  const schemaBundleText = await fetchVerifiedBytes(official, fetchImpl, byteStore, manifest.worldSchemaBundle).then(
    (bytes) => new TextDecoder().decode(bytes),
  );
  const schemaBundle: unknown = JSON.parse(schemaBundleText);

  const documents = buildLazySet<ManifestDocumentEntry>(official, fetchImpl, byteStore, manifest.documents);
  const composed = buildLazySet<ManifestComposedEntry>(official, fetchImpl, byteStore, manifest.composed);
  const assets = buildLazySet<ManifestAssetEntry>(
    official,
    fetchImpl,
    byteStore,
    manifest.assets,
    // Two families may legitimately share a bare name (a "table" and a "music" row both named
    // "score"); the manifest's own sort key is by name alone, so disambiguate the lookup key here.
    (entry) => `${entry.family}/${entry.name}`,
  );

  const engineEntry = manifest.engine.files.find((file: ManifestEngineFile) => file.name === manifest.engine.entry);
  if (!engineEntry) {
    throw new OfficialRefusal(`official manifest's engine.entry '${manifest.engine.entry}' names no engine.files entry.`);
  }

  const engineFiles: Record<string, OfficialFileRef> = {};
  for (const file of manifest.engine.files) {
    engineFiles[file.name] = { url: official.objectUrl(file.path).href, hash: file.hash };
  }

  return {
    manifest,
    build: manifest.build,
    schemaBundle,
    documents,
    composed,
    assets,
    engineEntryUrl: official.objectUrl(engineEntry.path).href,
    engineFiles,
    source,
  };
}
