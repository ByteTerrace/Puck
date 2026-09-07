// The engine host dispatcher: `'inline'` boots Puck.World.Browser directly in the calling thread (inlineHost.ts);
// `'worker'` boots it inside a dedicated module Worker (engine.worker.ts) and proxies every WorldEngine call across
// postMessage's structured clone, which carries `bigint` arguments and return values natively — no JSON
// re-encoding at this boundary, unlike the wasm boundary itself. Node has no Worker/DOM, so every test in this
// repository drives `'inline'` mode; `'worker'` mode is exercised by the studio itself in a real browser tab.
import { createInlineWorldEngine } from "./inlineHost";
import type { EngineHostOptions, WorldEngine } from "./engineTypes";

/** One request `engineHost.ts` posts to `engine.worker.ts`. `'call'.args` rides the postMessage structured clone
 * as-is (never JSON-stringified), so a `bigint` `tick`/`value` argument crosses intact. */
export type WorkerRequest =
  | { kind: "init"; engineEntryUrl: string }
  | { kind: "dispose" }
  | { kind: "call"; id: number; method: keyof WorldEngine; args: unknown[] };
/** One response `engine.worker.ts` posts back. `'result'.value` is whatever the wrapped `WorldEngine` method
 * resolved to — already bigint-typed by `inlineHost.wrapRawExports`, carried intact through structured clone. */
export type WorkerResponse =
  | { kind: "ready" }
  | { kind: "init-error"; error: string }
  | { kind: "result"; id: number; ok: true; value: unknown }
  | { kind: "result"; id: number; ok: false; error: string };

function createWorkerWorldEngine(options: EngineHostOptions): Promise<WorldEngine> {
  // resourceLoader cannot cross a Worker boundary — a function is not structured-clone-compatible — so 'worker'
  // mode always boots with dotnet.js' default fetch; see engineTypes.ts's own remarks on EngineResourceLoader.
  const worker = new Worker(new URL("./engine.worker.ts", import.meta.url), { type: "module" });
  let nextId = 1;
  const pending = new Map<number, { resolve: (value: unknown) => void; reject: (reason: Error) => void }>();

  worker.onmessage = (event: MessageEvent<WorkerResponse>) => {
    const message = event.data;

    if (message.kind !== "result") return;

    const waiter = pending.get(message.id);

    if (!waiter) return;

    pending.delete(message.id);

    if (message.ok) waiter.resolve(message.value);
    else waiter.reject(new Error(message.error));
  };

  function call(method: keyof WorldEngine, ...args: unknown[]): Promise<unknown> {
    const id = nextId++;

    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      worker.postMessage({ kind: "call", id, method, args } satisfies WorkerRequest);
    });
  }
  function buildProxy(): WorldEngine {
    return {
      version: () => call("version") as ReturnType<WorldEngine["version"]>,
      parse: (json) => call("parse", json) as ReturnType<WorldEngine["parse"]>,
      parseFragment: (fragmentJson, hostJson, alias) => call("parseFragment", fragmentJson, hostJson, alias) as ReturnType<WorldEngine["parseFragment"]>,
      composeTree: (rootName, documents, edited) => call("composeTree", rootName, documents, edited) as ReturnType<WorldEngine["composeTree"]>,
      canonicalize: (json) => call("canonicalize", json) as ReturnType<WorldEngine["canonicalize"]>,
      compile: (json) => call("compile", json) as ReturnType<WorldEngine["compile"]>,
      release: (handle) => call("release", handle) as ReturnType<WorldEngine["release"]>,
      rows: (handle) => call("rows", handle) as ReturnType<WorldEngine["rows"]>,
      rebind: (handle, json) => call("rebind", handle, json) as ReturnType<WorldEngine["rebind"]>,
      judge: (handle, tick) => call("judge", handle, tick) as ReturnType<WorldEngine["judge"]>,
      readRow: (handle, row, key) => call("readRow", handle, row, key) as ReturnType<WorldEngine["readRow"]>,
      writeRow: (handle, row, key, value, write) => call("writeRow", handle, row, key, value, write) as ReturnType<WorldEngine["writeRow"]>,
      evaluate: (handle, expression, kind, tick) => call("evaluate", handle, expression, kind, tick) as ReturnType<WorldEngine["evaluate"]>,
      boardMask: (handle, row) => call("boardMask", handle, row) as ReturnType<WorldEngine["boardMask"]>,
      stateHash: (handle) => call("stateHash", handle) as ReturnType<WorldEngine["stateHash"]>,
      cells: (topologyJson) => call("cells", topologyJson) as ReturnType<WorldEngine["cells"]>,
      dispose: async () => {
        worker.postMessage({ kind: "dispose" } satisfies WorkerRequest);
        worker.terminate();
      },
    };
  }

  return new Promise<WorldEngine>((resolve, reject) => {
    const onReady = (event: MessageEvent<WorkerResponse>) => {
      if (event.data.kind === "ready") {
        worker.removeEventListener("message", onReady);
        resolve(buildProxy());
      } else if (event.data.kind === "init-error") {
        worker.removeEventListener("message", onReady);
        reject(new Error(event.data.error));
      }
    };

    worker.addEventListener("message", onReady);
    worker.postMessage({ kind: "init", engineEntryUrl: options.engineEntryUrl } satisfies WorkerRequest);
  });
}

/** Creates a `WorldEngine` booted per `options.mode` — `'inline'` in the calling thread, `'worker'` inside a
 * dedicated module Worker. */
export async function createEngineHost(options: EngineHostOptions): Promise<WorldEngine> {
  return (options.mode === "inline" ? createInlineWorldEngine(options) : createWorkerWorldEngine(options));
}
