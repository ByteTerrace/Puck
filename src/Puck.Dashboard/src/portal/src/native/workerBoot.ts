// The boot core shared by inline-mode boot (engineBoot.ts, running in the calling thread) and
// worker-mode boot (engine.worker.ts, running inside its own Worker global scope): fetches and
// hash-verifies every official engine file through the byte store, then boots Puck.World.Browser
// entirely from those verified bytes. `main.mjs` is never fetched — its own relative
// `import "./_framework/dotnet.js"` cannot resolve against a content-addressed object URL (there
// is no "_framework/" directory on the official server, only "objects/sha256/…") — so this module
// replicates `main.mjs`'s own five-line `createEngine()` instead (see `bootEngineFromOfficialFiles`'s
// own remarks).
//
// A pure module — no `self`/`window`/DOM-global access of its own beyond `fetch`/`Response`/
// `crypto`, all available in a Worker too — so this exact boot path is provable under Node with a
// fake postMessage pair, proving `engine.worker.ts`'s own logic without a real Worker (see
// tests/engineBoot.test.cjs's own worker-mode-under-Node case).
import { createByteStore, type ByteStore } from "../official/byteStore";
import { OfficialRefusal, verifyBytes } from "../official/verify";
import { wrapRawExports, dynamicImport, type RawBrowserExports } from "./inlineHost";
import type { WorldEngine } from "./engineTypes";

export type FetchLike = typeof fetch;

export interface BootEngineFileRef {
  readonly url: string;
  readonly hash: string;
}

/** The subset of an `OfficialLoad` a boot needs — every engine file keyed by its manifest name
 * (e.g. "_framework/dotnet.js", "main.mjs"), exactly `OfficialLoad.engineFiles`'s own shape. */
export interface BootRequest {
  readonly engineFiles: Readonly<Record<string, BootEngineFileRef>>;
}

const DOTNET_JS_NAME = "_framework/dotnet.js";
const DOTNET_BOOT_JS_NAME = "_framework/dotnet.boot.js";
// main.mjs's own hard-coded assembly name (Puck.World.Browser/main.mjs) — this boot path
// replicates that file's own createEngine(), so the one constant it hard-codes is repeated here.
const MAIN_ASSEMBLY = "Puck.World.Browser.dll";

// The behaviors dotnet.js's own asset loader demands a SYNCHRONOUS URL STRING for — never a
// Promise, never a Response — asserting so immediately if violated ("loadBootResource response
// for 'dotnetjs' type should be a URL string"; verified against a real boot). Every member here
// is one of dotnet.d.ts's own `SingleAssetBehaviors` mapped to the `"dotnetjs"` resource type.
const STRING_URL_BEHAVIORS: ReadonlySet<string> = new Set([
  "js-module-dotnet",
  "js-module-native",
  "js-module-runtime",
  "js-module-threads",
  "js-module-diagnostics",
]);

interface VerifiedAsset {
  readonly bytes: Uint8Array;
  readonly contentType: string;
}

function contentTypeFor(name: string): string {
  if (name.endsWith(".wasm")) return "application/wasm";
  if (name.endsWith(".json")) return "application/json";
  return "text/javascript";
}

function basenameOf(name: string): string {
  return name.slice(name.lastIndexOf("/") + 1);
}

async function fetchVerified(
  fetchImpl: FetchLike,
  byteStore: ByteStore,
  name: string,
  ref: BootEngineFileRef,
): Promise<Uint8Array> {
  const stored = await byteStore.get(ref.hash);
  if (stored) return stored;

  const response = await fetchImpl(ref.url);
  if (!response.ok) {
    throw new OfficialRefusal(`engine boot: fetching engine file '${name}' failed: HTTP ${response.status}.`);
  }
  const bytes = new Uint8Array(await response.arrayBuffer());
  // A mismatch throws here, before the engine ever runs and before the byte store is touched —
  // the same "verify before cache, never cache on refusal" discipline officialClient.ts's own
  // fetchVerifiedBytes follows.
  await verifyBytes(name, bytes, ref.hash);
  await byteStore.put(ref.hash, bytes, contentTypeFor(name));
  return bytes;
}

/** True only under Node's own test runner — the one environment whose ESM loader refuses to
 * `import()` an `http(s):` module specifier outright (`ERR_UNSUPPORTED_ESM_URL_SCHEME`). A real
 * browser (main thread or Worker) imports an already-verified object URL directly — see
 * `moduleSpecifierFor`'s own remarks for why neither environment can use a `blob:`/`data:` URL
 * for THIS particular family of module. */
function isNodeRuntime(): boolean {
  const proc = (globalThis as { process?: { versions?: { node?: string } } }).process;
  return typeof proc?.versions?.node === "string";
}

// Node-only temp directory for materialized dotnet.js-family modules, created lazily and shared
// across every module of one boot (dotnet.native.js and dotnet.runtime.js are siblings that must
// resolve relative to the same directory dotnet.js itself was loaded from).
let nodeBootDir: Promise<string> | undefined;

/**
 * Node-only: materializes `bytes` to a real temporary file under a shared boot directory and
 * returns its `file:` URL. Every dotnet.js-family module (dotnet.js, dotnet.native.js,
 * dotnet.runtime.js, …) reads its own `import.meta.url` to resolve sibling asset names
 * (`new URL(name, scriptDirectory)`) or to call Node's own `createRequire(import.meta.url)`; both
 * refuse a `blob:`/`data:` base — verified empirically: `new URL('x', 'blob:...')`,
 * `new URL('x', 'data:...')`, and `createRequire('data:...')` all throw, because those schemes
 * have an "opaque path" under the URL Standard, not a hierarchical one a relative reference can
 * resolve against. Only a real hierarchical URL (`http(s):` or `file:`) works, and Node's loader
 * refuses `http(s):` specifiers outright — so `file:` is the only option left here.
 *
 * The two module specifiers below ("node:fs/promises" etc.) are deliberately NOT string literals:
 * TypeScript then types the dynamic `import()` result as `any` instead of trying to resolve
 * Node's own type declarations, so this file needs no `@types/node` dependency. Vite's bundler
 * likewise leaves a non-literal dynamic import unresolved at build time — inert unless actually
 * reached at runtime, which only Node's own boot path (this function) ever does.
 */
async function materializeToFileUrl(basename: string, bytes: Uint8Array): Promise<string> {
  // Cast to `any`, never `typeof import("node:fs/promises")`: a TYPE-position `import()` of a
  // Node module DOES need `@types/node` resolvable, even though the VALUE-position dynamicImport
  // call above needs no declaration file at all — this file stays free of that dependency either way.
  const fs: any = await dynamicImport("node:fs/promises");
  const os: any = await dynamicImport("node:os");
  const path: any = await dynamicImport("node:path");
  const { pathToFileURL }: any = await dynamicImport("node:url");

  nodeBootDir ??= fs.mkdtemp(path.join(os.tmpdir(), "puck-engine-boot-"));
  const dir = await nodeBootDir;
  const file = path.join(dir, basename);
  await fs.writeFile(file, bytes);
  return pathToFileURL(file).href as string;
}

/**
 * Resolves the specifier used to `import()` a dotnet.js-family module: under Node, a real temp
 * `file:` URL for the verified bytes (`materializeToFileUrl`); everywhere else (a real browser's
 * main thread or a Worker), the file's own already-verified object URL — a normal hierarchical
 * `https:` URL, importable directly. That second `import()` may cause one more network round trip
 * (typically served from the HTTP cache), but it trusts nothing new: this exact URL's bytes were
 * already hash-checked against the manifest before this function is ever called.
 */
async function moduleSpecifierFor(name: string, ref: BootEngineFileRef, bytes: Uint8Array): Promise<string> {
  return isNodeRuntime() ? materializeToFileUrl(basenameOf(name), bytes) : ref.url;
}

let sharedDefaultByteStore: ByteStore | undefined;
/** The byte store `bootEngineFromOfficialFiles` falls back to when its caller supplies none —
 * one instance per JS realm (main thread, or a Worker's own separate global scope), so repeated
 * boots in the same realm share a warm cache exactly as a real browser's Cache Storage already
 * does across callers by name. */
function defaultByteStore(): ByteStore {
  sharedDefaultByteStore ??= createByteStore();
  return sharedDefaultByteStore;
}

/**
 * Fetches and hash-verifies every file in `request.engineFiles`, then boots Puck.World.Browser
 * from the verified `_framework/dotnet.js` bytes with a resourceLoader answered entirely from
 * those bytes — replicating `main.mjs`'s own five lines (`withResourceLoader` → `create()` →
 * `getAssemblyExports("Puck.World.Browser.dll")` → `.Puck.World.Browser.Exports.BrowserExports`).
 *
 * `disposeCore`, when given, is invoked from the returned engine's own `dispose()` — used by
 * `engine.worker.ts` to close the worker once the caller is done with it, exactly as
 * `inlineHost.wrapRawExports` already supports for the plain (non-official) worker boot.
 */
export async function bootEngineFromOfficialFiles(
  request: BootRequest,
  fetchImpl: FetchLike,
  byteStore: ByteStore = defaultByteStore(),
  disposeCore?: () => void,
): Promise<WorldEngine> {
  const dotnetJsRef = request.engineFiles[DOTNET_JS_NAME];
  if (!dotnetJsRef) {
    throw new OfficialRefusal(`engine boot: official manifest names no engine file '${DOTNET_JS_NAME}'.`);
  }

  // Every engine file is fetched and verified up front — never lazily on the resourceLoader's
  // first ask — so a tampered object is refused by name before the engine ever runs, and a warm
  // byte store answers every subsequent boot (including this one's own module imports below)
  // without a single further network fetch.
  const assets = new Map<string, VerifiedAsset>();
  await Promise.all(
    Object.entries(request.engineFiles).map(async ([name, ref]) => {
      const bytes = await fetchVerified(fetchImpl, byteStore, name, ref);
      assets.set(name, { bytes, contentType: contentTypeFor(name) });
    }),
  );

  const byBasename = new Map<string, VerifiedAsset>();
  for (const [name, asset] of assets) {
    byBasename.set(basenameOf(name), asset);
  }

  // dotnet.js' asset loader asserts a synchronous string for STRING_URL_BEHAVIORS members, so
  // every dotnet.js-family sibling module's specifier is resolved up front — never awaited inside
  // the resourceLoader callback itself, which must answer those cases synchronously.
  const jsModuleSpecifiers = new Map<string, string>();
  for (const [name, ref] of Object.entries(request.engineFiles)) {
    const basename = basenameOf(name);
    if (basename === "dotnet.js" || basename === "dotnet.boot.js") continue;
    if (!basename.startsWith("dotnet.") || !basename.endsWith(".js")) continue;
    jsModuleSpecifiers.set(basename, await moduleSpecifierFor(name, ref, assets.get(name)!.bytes));
  }

  const dotnetJsSpecifier = await moduleSpecifierFor(DOTNET_JS_NAME, dotnetJsRef, assets.get(DOTNET_JS_NAME)!.bytes);

  // dotnet.boot.js is an ES module ("export const config = {...}"), not JSON — the generic
  // fetch()+JSON.parse() path 'manifest'-behaved resources otherwise take cannot read it, so this
  // resourceLoader returns the parsed config directly (the `Promise<BootModule>` arm of
  // `LoadBootResourceCallback`). A `data:` URL is fine for this one import — unlike the
  // dotnet.js-family modules above, dotnet.boot.js has no `import.meta.url`-relative logic of its
  // own to break. `resources.wasmSymbols` is stripped: it names `dotnet.native.js.symbols`, a
  // debug-only file the official manifest never packages (see this module's own remarks below the
  // resourceLoader), and dotnet.js otherwise fetches it unconditionally — outside the
  // resourceLoader dispatch entirely — and treats a failed fetch as fatal.
  const bootJsRef = request.engineFiles[DOTNET_BOOT_JS_NAME];
  let manifestConfig: Promise<{ config: unknown }> | undefined;
  if (bootJsRef) {
    const bootBytes = assets.get(DOTNET_BOOT_JS_NAME)!.bytes;
    const dataUrl = `data:text/javascript;base64,${bytesToBase64(bootBytes)}`;
    manifestConfig = dynamicImport(dataUrl).then((bootModule) => {
      const config = (bootModule as { config: Record<string, unknown> }).config;
      const resources = { ...(config.resources as Record<string, unknown>) };
      delete resources.wasmSymbols;
      return { config: { ...config, resources } };
    });
  }

  function resourceLoader(
    type: string,
    name: string,
    _defaultUri: string,
    _integrity: string,
    behavior: string,
  ): string | Promise<Response> | Promise<{ config: unknown }> {
    if (type === "manifest") {
      if (!manifestConfig) {
        throw new OfficialRefusal(`engine boot: official manifest names no engine file '${DOTNET_BOOT_JS_NAME}'.`);
      }
      return manifestConfig;
    }
    if (STRING_URL_BEHAVIORS.has(behavior)) {
      const specifier = jsModuleSpecifiers.get(name);
      if (!specifier) {
        throw new OfficialRefusal(`engine boot: no verified module for resource '${name}' (behavior '${behavior}').`);
      }
      return specifier;
    }
    const asset = byBasename.get(name);
    if (!asset) {
      throw new OfficialRefusal(`engine boot: no verified bytes for resource '${name}' (type '${type}', behavior '${behavior}').`);
    }
    // 'application/wasm' on every .wasm response is mandatory for WebAssembly.instantiateStreaming;
    // contentTypeFor already guarantees it. Uint8Array satisfies BodyInit at runtime, but this
    // lib's DOM types don't say so (see byteStore.ts's own remarks) — hand Response the plain
    // ArrayBuffer copy instead of fighting that typing gap.
    return Promise.resolve(new Response(asset.bytes.slice().buffer, { headers: { "content-type": asset.contentType } }));
  }

  const dotnetModule = (await dynamicImport(dotnetJsSpecifier)) as { dotnet: DotnetHostBuilderLike };
  const builder = dotnetModule.dotnet.withResourceLoader(resourceLoader);
  const { getAssemblyExports } = await builder.create();
  const exports = (await getAssemblyExports(MAIN_ASSEMBLY)) as { Puck: { World: { Browser: { Exports: { BrowserExports: RawBrowserExports } } } } };
  const raw = exports.Puck.World.Browser.Exports.BrowserExports;

  return wrapRawExports(raw, disposeCore);
}

// The narrow slice of dotnet.d.ts's own DotnetHostBuilder/RuntimeAPI this boot path calls —
// declared locally rather than imported, since dotnet.d.ts ships only inside the .NET SDK's
// browser-wasm runtime pack, never as an npm package this workspace could depend on.
interface DotnetHostBuilderLike {
  withResourceLoader(loader: (type: string, name: string, defaultUri: string, integrity: string, behavior: string) => string | Promise<Response> | Promise<{ config: unknown }>): DotnetHostBuilderLike;
  create(): Promise<{ getAssemblyExports(assemblyName: string): Promise<unknown> }>;
}

function bytesToBase64(bytes: Uint8Array): string {
  const globalBuffer = (globalThis as { Buffer?: { from(bytes: Uint8Array): { toString(encoding: string): string } } }).Buffer;
  if (globalBuffer) return globalBuffer.from(bytes).toString("base64");

  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}
