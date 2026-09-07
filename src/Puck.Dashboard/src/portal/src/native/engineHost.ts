// The engine host dispatcher: `'inline'` boots Puck.World.Browser directly in the calling thread (inlineHost.ts);
// `'worker'` boots it inside a dedicated module Worker (engine.worker.ts) and proxies every WorldEngine call across
// postMessage's structured clone, which carries `bigint` arguments and return values natively — no JSON
// re-encoding at this boundary, unlike the wasm boundary itself. Node has no Worker/DOM, so every test in this
// repository drives `'inline'` mode; `'worker'` mode is exercised by the studio itself in a real browser tab.
//
// The actual Worker construction lives in engineWorkerLauncher.ts, reached only through a dynamic
// `import()` inside `createEngineHost`'s own `'worker'` branch below — never a static top-level
// import. That module reads `import.meta.url`, which has no CommonJS equivalent; a static import
// here would make Node's loader treat this whole file as an ES module the moment anything requires
// it, breaking every test in this repository that requires engineHost.ts under
// `require.extensions['.ts']`'s CommonJS transpile (see engineWorkerLauncher.ts's own remarks).
import { createInlineWorldEngine } from "./inlineHost";
import type { EngineHostOptions, WorldEngine } from "./engineTypes";

/** One request posted to `engine.worker.ts`, both kinds sent by `engineWorkerLauncher.createWorkerEngine`.
 * `'init'` boots a plain `main.mjs` URL (`createEngineHost`'s own `'worker'` path); `'boot'` boots
 * from an official tree's engine files instead (`engineBoot.bootEngineFromOfficial`'s worker-mode
 * path) — only plain data crosses for `'boot'`, since a `fetchImpl` function cannot ride
 * postMessage, so the worker fetches and hash-verifies with its own global `fetch` (see
 * workerBoot.ts's own remarks). `'call'.args` rides the postMessage structured clone as-is (never
 * JSON-stringified), so a `bigint` `tick`/`value` argument crosses intact. */
export type WorkerRequest =
  | { kind: "init"; engineEntryUrl: string }
  | { kind: "boot"; engineFiles: Readonly<Record<string, { url: string; hash: string }>> }
  | { kind: "dispose" }
  | { kind: "call"; id: number; method: keyof WorldEngine; args: unknown[] };
/** One response `engine.worker.ts` posts back. `'result'.value` is whatever the wrapped `WorldEngine` method
 * resolved to — already bigint-typed by `inlineHost.wrapRawExports`, carried intact through structured clone. */
export type WorkerResponse =
  | { kind: "ready" }
  | { kind: "init-error"; error: string }
  | { kind: "result"; id: number; ok: true; value: unknown }
  | { kind: "result"; id: number; ok: false; error: string };

/** Creates a `WorldEngine` booted per `options.mode` — `'inline'` in the calling thread, `'worker'` inside a
 * dedicated module Worker. `resourceLoader` cannot cross a Worker boundary — a function is not
 * structured-clone-compatible — so `'worker'` mode always boots with dotnet.js' default fetch; see
 * engineTypes.ts's own remarks on `EngineResourceLoader`. */
export async function createEngineHost(options: EngineHostOptions): Promise<WorldEngine> {
  if (options.mode === "inline") return createInlineWorldEngine(options);

  const { createWorkerEngine } = await import("./engineWorkerLauncher");
  return createWorkerEngine({ kind: "init", engineEntryUrl: options.engineEntryUrl });
}
