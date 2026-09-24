// The boot core shared by inline-mode boot (engineBoot.ts, running in the calling thread) and
// worker-mode boot (engine.worker.ts, running inside its own Worker global scope): fetches and
// hash-verifies every official engine file through the byte store, then boots Puck.World.Browser
// entirely from those verified bytes. The wasm is compiled once per session: a browser streams it with
// its hash as SRI integrity, and a boot handed another engine's compiled module compiles nothing
// (`engineWasmResponse`). `main.mjs` is never fetched — its own relative
// `import "./_framework/dotnet.js"` cannot resolve against a content-addressed object URL (there
// is no "_framework/" directory on the official server, only "objects/sha256/…") — so this module
// replicates `main.mjs`'s own five-line `createEngine()` instead (see `bootEngineFromOfficialFiles`'s
// own remarks).
//
// A pure module — no `self`/`window`/DOM-global access of its own beyond `fetch`/`Response`/
// `crypto`, all available in a Worker too — so this exact boot path is provable under Node with a
// fake postMessage pair, proving `engine.worker.ts`'s own logic without a real Worker (see
// tests/engineBoot.test.cjs's own worker-mode-under-Node case).
import { createByteStore, readThroughVerified, type ByteStore } from "../official/byteStore";
import { OfficialRefusal, verifyBytes } from "../official/verify";
import { wrapRawExports, dynamicImport, type RawBrowserExports } from "./inlineHost";
import type { EngineCore } from "./engineTypes";

export type FetchLike = typeof fetch;

export interface BootEngineFileRef {
  readonly url: string;
  readonly hash: string;
}

/** The subset of an `OfficialLoad` a boot needs — every engine file keyed by its manifest name
 * (e.g. "_framework/dotnet.js", "main.mjs"), exactly `OfficialLoad.engineFiles`'s own shape. */
export interface BootRequest {
  readonly engineFiles: Readonly<Record<string, BootEngineFileRef>>;
  /** The compiled `dotnet.native.wasm` another engine of this session already holds. A boot given one instantiates
   * it and compiles nothing; a boot without one compiles the module once and hands it back on the engine. */
  readonly wasmModule?: WebAssembly.Module;
}

const DOTNET_JS_NAME = "_framework/dotnet.js";
const DOTNET_BOOT_JS_NAME = "_framework/dotnet.boot.js";
const DOTNET_WASM_NAME = "dotnet.native.wasm";
// The asset behavior dotnet.js gives its own download of that file.
const DOTNET_WASM_BEHAVIOR = "dotnetwasm";
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
  // A stored copy answers only while it matches the manifest, and a mismatch refuses before the engine ever runs —
  // the same read-through discipline officialClient.ts's own fetchVerifiedBytes follows.
  return readThroughVerified(byteStore, name, ref.hash, contentTypeFor(name), async () => {
    const response = await fetchImpl(ref.url);
    if (!response.ok) {
      throw new OfficialRefusal(`engine boot: fetching engine file '${name}' failed: HTTP ${response.status}.`);
    }
    return new Uint8Array(await response.arrayBuffer());
  });
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

/** The Subresource Integrity value for a manifest hash: `sha256/<hex>` becomes `sha256-<base64>`. */
export function integrityFor(hash: string): string {
  const hex = hash.slice(hash.indexOf("/") + 1);
  const bytes = new Uint8Array(hex.length / 2);
  for (let index = 0; index < bytes.length; index++) bytes[index] = Number.parseInt(hex.slice(index * 2, index * 2 + 2), 16);
  return `sha256-${bytesToBase64(bytes)}`;
}

/**
 * What compiling one wasm response this boot handed dotnet.js should do: answer with a module another engine
 * already compiled, or compile and report the module back.
 */
interface WasmResponsePlan {
  readonly supplied?: WebAssembly.Module;
  readonly compiled?: (module: WebAssembly.Module) => void;
  /** Settles once the response's bytes are checked against the manifest's hash; the compile answers only then. */
  readonly verified?: Promise<void>;
}

// The wasm responses this realm's boots handed dotnet.js, by identity.
const wasmResponses = new WeakMap<Response, WasmResponsePlan>();
let compilesSupervised = false;

/**
 * dotnet.js compiles `dotnet.native.wasm` itself, streaming the response the resource loader hands it, and binds its
 * runtime's own imports before instantiating; a caller-supplied `instantiateWasm` skips that binding, so the module
 * has to travel through dotnet.js' own compile. This wraps `WebAssembly.compileStreaming` once per realm: a response
 * this boot planned answers with the supplied module and compiles nothing, or compiles and reports the module back.
 * The compile streams while the bytes are hashed, and answers only once they match the manifest, so a hash mismatch
 * is refused by name rather than by whatever the compiler makes of tampered bytes. Every other call passes through
 * unchanged.
 */
function superviseWasmCompiles(): void {
  if (compilesSupervised) return;
  compilesSupervised = true;
  const compileStreaming = WebAssembly.compileStreaming.bind(WebAssembly);
  WebAssembly.compileStreaming = async (source: Response | PromiseLike<Response>) => {
    const response = await source;
    const plan = wasmResponses.get(response);
    if (plan?.supplied) return plan.supplied;
    const compiling = compileStreaming(response);
    if (plan?.verified) {
      compiling.catch(() => undefined);
      await plan.verified;
    }
    const module = await compiling;
    plan?.compiled?.(module);
    return module;
  };
}

/** Registers `response` as the wasm this boot hands dotnet.js, under `plan`. */
function planWasmResponse(response: Response, plan: WasmResponsePlan): Response {
  superviseWasmCompiles();
  wasmResponses.set(response, plan);
  return response;
}

const WASM_HEADERS = { "content-type": "application/wasm" };

/**
 * The wasm response for one boot. Given a module, an empty stand-in that compiles to it. Given verified bytes (Node,
 * where the bytes were fetched and hash-checked up front), those bytes. In a browser, the content-addressed object
 * URL fetched with the manifest's hash as its SRI integrity, so the browser verifies the bytes and the HTTP cache and
 * the wasm code cache can apply. The studio checks the bytes against the manifest itself as well, with the official
 * client's own `verifyBytes`, since a fetch may ignore `integrity`: the compile answers only once they match, and
 * only matching bytes are stored. When the network fails, the stored copy is verified the same way before it answers.
 * Every mismatch, and a failed fetch with nothing stored, is refused by name.
 */
export async function engineWasmResponse(
  name: string,
  ref: BootEngineFileRef,
  fetchImpl: FetchLike,
  byteStore: ByteStore,
  supplied: WebAssembly.Module | undefined,
  verifiedBytes: Uint8Array | undefined,
  compiled: (module: WebAssembly.Module) => void,
): Promise<Response> {
  if (supplied) return planWasmResponse(new Response(new Uint8Array(0).buffer, { headers: WASM_HEADERS }), { supplied });
  if (verifiedBytes) return planWasmResponse(new Response(verifiedBytes.slice().buffer, { headers: WASM_HEADERS }), { compiled });
  const integrity = integrityFor(ref.hash);
  try {
    const response = await fetchImpl(ref.url, { integrity });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const verified = response.clone().arrayBuffer().then(async (buffer) => {
      const bytes = await verifyBytes(name, new Uint8Array(buffer), ref.hash);
      if (!(await byteStore.has(ref.hash))) await byteStore.put(ref.hash, bytes, "application/wasm");
    });
    return planWasmResponse(response, { compiled, verified });
  } catch (error) {
    const stored = await byteStore.get(ref.hash);
    if (!stored) {
      throw new OfficialRefusal(
        `engine boot: engine file '${name}' could not be fetched with integrity '${integrity}', and no verified copy is ` +
          `cached: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
    const bytes = await verifyBytes(name, stored, ref.hash);
    return planWasmResponse(new Response(bytes.slice().buffer, { headers: WASM_HEADERS }), { compiled });
  }
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
 * Fetches and hash-verifies every file in `request.engineFiles` (the wasm as `engineWasmResponse` says), then
 * boots Puck.World.Browser from the verified `_framework/dotnet.js` bytes with a resourceLoader answered entirely
 * from those bytes, and returns the engine with the module its runtime runs — replicating `main.mjs`'s own five lines (`withResourceLoader` → `create()` →
 * `getAssemblyExports("Puck.World.Browser.dll")` → `.Puck.World.Browser.Exports.BrowserExports`).
 *
 * `disposeCore`, when given, is invoked from the returned engine's own `dispose()` — used by
 * `engine.worker.ts` to close the worker once the caller is done with it, exactly as
 * `inlineHost.wrapRawExports` already supports for the plain (non-official) worker boot.
 */
/** The runtimes this JS realm hosts, by the dotnet.js module specifier each was created from. A module
 * specifier names one module instance, and dotnet.js refuses a second `create()` on the same instance
 * ("Runtime module already loaded") — so a repeated boot of the same engine (React's StrictMode
 * double-mount, an HMR remount, a crashed-and-remounted actor) joins the existing boot instead. Under
 * Node every boot materializes a fresh temp file, so every boot there is its own instance. */
const realmRuntimes = new Map<string, Promise<EngineCore>>();

export async function bootEngineFromOfficialFiles(
  request: BootRequest,
  fetchImpl: FetchLike,
  byteStore: ByteStore = defaultByteStore(),
  disposeCore?: () => void,
): Promise<EngineCore> {
  const dotnetJsRef = request.engineFiles[DOTNET_JS_NAME];
  if (!dotnetJsRef) {
    throw new OfficialRefusal(`engine boot: official manifest names no engine file '${DOTNET_JS_NAME}'.`);
  }

  // Every engine file is fetched and verified up front — never lazily on the resourceLoader's
  // first ask — so a tampered object is refused by name before the engine ever runs, and a warm
  // byte store answers every subsequent boot (including this one's own module imports below)
  // without a single further network fetch.
  // The wasm is the exception: a boot handed a compiled module needs none of its bytes, and a browser streams it
  // into dotnet.js' one compile (`engineWasmResponse`), verified by the browser against the manifest's hash.
  const wasmName = Object.keys(request.engineFiles).find((name) => basenameOf(name) === DOTNET_WASM_NAME);
  const streamsWasm = request.wasmModule !== undefined || !isNodeRuntime();
  const assets = new Map<string, VerifiedAsset>();
  await Promise.all(
    Object.entries(request.engineFiles).map(async ([name, ref]) => {
      if (name === wasmName && streamsWasm) return;
      const bytes = await fetchVerified(fetchImpl, byteStore, name, ref);
      assets.set(name, { bytes, contentType: contentTypeFor(name) });
    }),
  );
  if (!wasmName) {
    throw new OfficialRefusal(`engine boot: official manifest names no engine file '${DOTNET_WASM_NAME}'.`);
  }
  const wasmFile: string = wasmName;
  let compiled: WebAssembly.Module | undefined = request.wasmModule;

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
    if (behavior === DOTNET_WASM_BEHAVIOR) {
      return engineWasmResponse(wasmFile, request.engineFiles[wasmFile], fetchImpl, byteStore, request.wasmModule, assets.get(wasmFile)?.bytes, (module) => {
        compiled = module;
      });
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

  const existing = realmRuntimes.get(dotnetJsSpecifier);
  if (existing) return existing;
  if (realmRuntimes.size > 0 && !isNodeRuntime()) {
    throw new OfficialRefusal("engine boot: this page already hosts a different engine build; reload the page to switch engines.");
  }

  const booted = (async () => {
    const dotnetModule = (await dynamicImport(dotnetJsSpecifier)) as { dotnet: DotnetHostBuilderLike };
    const builder = dotnetModule.dotnet.withResourceLoader(resourceLoader);
    const { getAssemblyExports } = await builder.create();
    const exports = (await getAssemblyExports(MAIN_ASSEMBLY)) as { Puck: { World: { Browser: { Exports: { BrowserExports: RawBrowserExports } } } } };
    const raw = exports.Puck.World.Browser.Exports.BrowserExports;

    return { ...wrapRawExports(raw, disposeCore), wasmModule: compiled };
  })();
  realmRuntimes.set(dotnetJsSpecifier, booted);
  booted.catch(() => {
    if (realmRuntimes.get(dotnetJsSpecifier) === booted) realmRuntimes.delete(dotnetJsSpecifier);
  });

  return booted;
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
