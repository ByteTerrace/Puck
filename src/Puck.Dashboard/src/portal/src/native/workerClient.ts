import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest, WorkerResponse } from "./engineHost";

type EngineWorker = Pick<Worker, "addEventListener" | "removeEventListener" | "postMessage" | "terminate">;

/** Owns a worker until disposal; every outstanding call settles on cancellation or failure. */
export function connectWorkerEngine(worker: EngineWorker, bootRequest: WorkerRequest, signal?: AbortSignal): Promise<WorldEngine> {
  let nextId = 1;
  let closed: Error | null = null;
  let ready = false;
  const pending = new Map<number, { resolve: (value: unknown) => void; reject: (reason: Error) => void }>();
  let resolveBoot!: (engine: WorldEngine) => void;
  let rejectBoot!: (error: Error) => void;
  const boot = new Promise<WorldEngine>((resolve, reject) => { resolveBoot = resolve; rejectBoot = reject; });

  function close(error: Error) {
    if (closed) return;
    closed = error;
    worker.removeEventListener("message", onMessage);
    worker.removeEventListener("error", onError);
    worker.removeEventListener("messageerror", onMessageError);
    signal?.removeEventListener("abort", onAbort);
    worker.terminate();
    rejectBoot(error);
    for (const waiter of pending.values()) waiter.reject(error);
    pending.clear();
  }
  function onAbort() { close(new Error("Engine worker boot cancelled.")); }
  function onError(event: ErrorEvent) { close(new Error(event.message || "Engine worker failed.")); }
  function onMessageError() { close(new Error("Engine worker response could not be decoded.")); }
  function onMessage(event: MessageEvent<WorkerResponse>) {
    const message = event.data;
    if (message.kind === "ready" && !ready) {
      ready = true;
      signal?.removeEventListener("abort", onAbort);
      resolveBoot(buildProxy());
    } else if (message.kind === "init-error") {
      close(new Error(message.error));
    } else if (message.kind === "result") {
      const waiter = pending.get(message.id);
      if (!waiter) return;
      pending.delete(message.id);
      if (message.ok) waiter.resolve(message.value);
      else waiter.reject(new Error(message.error));
    }
  }
  function call(method: keyof WorldEngine, ...args: unknown[]): Promise<unknown> {
    if (closed) return Promise.reject(closed);
    const id = nextId++;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      try { worker.postMessage({ kind: "call", id, method, args } satisfies WorkerRequest); }
      catch (error) { pending.delete(id); reject(error); }
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
      dispose: async () => close(new Error("Engine worker disposed.")),
    };
  }

  worker.addEventListener("message", onMessage);
  worker.addEventListener("error", onError);
  worker.addEventListener("messageerror", onMessageError);
  signal?.addEventListener("abort", onAbort, { once: true });
  if (signal?.aborted) onAbort();
  if (!closed) {
    try { worker.postMessage(bootRequest); }
    catch (error) { close(error instanceof Error ? error : new Error(String(error))); }
  }
  return boot;
}
