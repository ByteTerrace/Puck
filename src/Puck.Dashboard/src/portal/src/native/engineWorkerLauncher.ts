// Kept apart from engineHost.ts/engineBoot.ts deliberately: this is the ONE place either file's
// `'worker'` mode reads `import.meta.url` (to locate engine.worker.ts relative to this bundle) —
// a genuine ES-module-only construct with no CommonJS equivalent, so any `require()` of a file
// containing it (even inside a function nobody calls) makes Node's module loader treat the whole
// file as an ES module and refuse to run it via a CommonJS `_compile` hook (verified: Node's
// module-format detection inspects a file's full source text, not just the code paths actually
// executed). Both `engineHost.createEngineHost` and `engineBoot.bootEngineFromOfficial` reach this
// module through a dynamic `import()` inside their own `'worker'` branches — never a static
// top-level import — so merely requiring either of THOSE files under Node (every test in this
// repository) never loads this one at all; only an actual `'worker'`-mode boot does, and that
// only ever happens in a real browser tab anyway (Node has no `Worker` global either).
//
// `createWorkerEngine` is the ONE Worker-construction-and-proxy implementation shared by both
// callers: they differ only in the first message posted to the worker (`'init'` boots a plain
// `main.mjs` URL; `'boot'` boots from an official tree's engine files) — everything after that
// (the ready/init-error handshake, the postMessage call proxy) is identical, so it lives here once.
import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest, WorkerResponse } from "./engineHost";

/**
 * Boots a `WorldEngine` inside a dedicated module Worker, posting `bootRequest` as the worker's
 * first message (`{kind:"init", engineEntryUrl}` or `{kind:"boot", engineFiles}`) and proxying
 * every subsequent `WorldEngine` call across postMessage's structured clone, which carries
 * `bigint` arguments and return values natively.
 */
export function createWorkerEngine(bootRequest: WorkerRequest): Promise<WorldEngine> {
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
    const onMessage = (event: MessageEvent<WorkerResponse>) => {
      if (event.data.kind === "ready") {
        worker.removeEventListener("message", onMessage);
        resolve(buildProxy());
      } else if (event.data.kind === "init-error") {
        worker.removeEventListener("message", onMessage);
        worker.terminate();
        reject(new Error(event.data.error));
      }
    };

    worker.addEventListener("message", onMessage);
    worker.postMessage(bootRequest);
  });
}
