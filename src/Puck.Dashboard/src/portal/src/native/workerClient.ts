import { AsyncSubject, defer, EMPTY, filter, first, firstValueFrom, fromEvent, map, merge, mergeMap, type Observable, of, share, takeUntil, throwError } from "rxjs";
import type { EngineCore, WorldEngine } from "./engineTypes";
import { withLanguageServer } from "./languagePump";
import type { WorkerRequest, WorkerResponse } from "./engineHost";

type EngineWorker = Pick<Worker, "addEventListener" | "removeEventListener" | "postMessage" | "terminate">;
type WorkerResult = Extract<WorkerResponse, { kind: "result" }>;

const toError = (reason: unknown) => (reason instanceof Error ? reason : new Error(String(reason)));

/**
 * Owns a worker until it closes. The worker's replies are one stream, demultiplexed by call id; closing is
 * terminal and happens once, on the first of a load error, an undecodable message, an initialization refusal, an
 * abort before boot, or disposal. It terminates the worker and settles boot and every outstanding call with that
 * error, and every later call rejects with it too.
 */
export function connectWorkerEngine(worker: EngineWorker, bootRequest: WorkerRequest, signal?: AbortSignal): Promise<WorldEngine> {
  const closing = new AsyncSubject<Error>();
  let closedWith: Error | null = null;
  const close = (error: Error) => {
    if (closedWith) return;
    closedWith = error;
    worker.terminate();
    closing.next(error);
    closing.complete();
  };
  // Errors with the closing reason, immediately for a subscriber that arrives after the close.
  const closed$: Observable<never> = closing.pipe(mergeMap((error) => throwError(() => error)));
  // Never completes on its own: a consumer waiting on `first()` must see the closing error, not an empty stream.
  // `share()` detaches the listener once the last consumer (each ends with the close) is gone.
  const messages$ = fromEvent<MessageEvent<WorkerResponse>>(worker, "message").pipe(
    map((event) => event.data),
    share(),
  );

  merge(
    fromEvent<ErrorEvent>(worker, "error").pipe(map((event) => new Error(event.message || "Engine worker failed."))),
    fromEvent(worker, "messageerror").pipe(map(() => new Error("Engine worker response could not be decoded."))),
    messages$.pipe(
      filter((message) => message.kind === "init-error"),
      map((message) => new Error(message.error)),
    ),
  )
    .pipe(takeUntil(closing))
    .subscribe(close);

  let nextId = 1;
  const call = (method: keyof EngineCore, ...args: unknown[]): Promise<unknown> => {
    const id = nextId++;
    const reply$ = messages$.pipe(
      first((message): message is WorkerResult => message.kind === "result" && message.id === id),
      map((result) => {
        if (!result.ok) throw new Error(result.error);
        return result.value;
      }),
    );
    // Listen before posting; a message that cannot be cloned fails only its own call.
    const post$ = defer(() => {
      if (!closedWith) worker.postMessage({ args, id, kind: "call", method } satisfies WorkerRequest);
      return EMPTY;
    });

    return firstValueFrom(merge(reply$, closed$, post$));
  };

  const engine: WorldEngine = withLanguageServer({
    analyzeCosts: (json) => call("analyzeCosts", json) as ReturnType<WorldEngine["analyzeCosts"]>,
    boardMask: (handle, row) => call("boardMask", handle, row) as ReturnType<WorldEngine["boardMask"]>,
    canonicalize: (json) => call("canonicalize", json) as ReturnType<WorldEngine["canonicalize"]>,
    cells: (topologyJson) => call("cells", topologyJson) as ReturnType<WorldEngine["cells"]>,
    compile: (json) => call("compile", json) as ReturnType<WorldEngine["compile"]>,
    costs: (handle) => call("costs", handle) as ReturnType<WorldEngine["costs"]>,
    dispose: async () => close(new Error("Engine worker disposed.")),
    evaluate: (handle, expression, kind, tick) => call("evaluate", handle, expression, kind, tick) as ReturnType<WorldEngine["evaluate"]>,
    judge: (handle, tick) => call("judge", handle, tick) as ReturnType<WorldEngine["judge"]>,
    parse: (json) => call("parse", json) as ReturnType<WorldEngine["parse"]>,
    parseFragment: (fragmentJson, hostJson, alias) => call("parseFragment", fragmentJson, hostJson, alias) as ReturnType<WorldEngine["parseFragment"]>,
    readRow: (handle, row, key) => call("readRow", handle, row, key) as ReturnType<WorldEngine["readRow"]>,
    rebind: (handle, json) => call("rebind", handle, json) as ReturnType<WorldEngine["rebind"]>,
    release: (handle) => call("release", handle) as ReturnType<WorldEngine["release"]>,
    rows: (handle) => call("rows", handle) as ReturnType<WorldEngine["rows"]>,
    stateHash: (handle) => call("stateHash", handle) as ReturnType<WorldEngine["stateHash"]>,
    version: () => call("version") as ReturnType<WorldEngine["version"]>,
    writeRow: (handle, row, key, value, write) => call("writeRow", handle, row, key, value, write) as ReturnType<WorldEngine["writeRow"]>,
    mountSources: (files) => call("mountSources", files) as ReturnType<WorldEngine["mountSources"]>,
    writeSource: (path, text) => call("writeSource", path, text) as ReturnType<WorldEngine["writeSource"]>,
    compileSource: (path) => call("compileSource", path) as ReturnType<WorldEngine["compileSource"]>,
    composeSource: (path) => call("composeSource", path) as ReturnType<WorldEngine["composeSource"]>,
    lsp: (message) => call("lsp", message) as ReturnType<WorldEngine["lsp"]>,
    lspIdle: () => call("lspIdle") as ReturnType<WorldEngine["lspIdle"]>,
    wasmCounts: () => call("wasmCounts") as ReturnType<WorldEngine["wasmCounts"]>,
  });

  // A worker that booted from an official build hands back its compiled module, for the session's next engine.
  const ready$ = messages$.pipe(
    first((message): message is Extract<WorkerResponse, { kind: "ready" }> => message.kind === "ready"),
    map((message) => (message.wasmModule ? { ...engine, wasmModule: message.wasmModule } : engine)),
  );

  // Aborting cancels the boot only; once the engine is ready, its owner disposes it instead.
  if (signal) {
    merge(fromEvent(signal, "abort"), signal.aborted ? of(null) : EMPTY)
      .pipe(first(), takeUntil(merge(ready$, closing)))
      .subscribe(() => close(new Error("Engine worker boot cancelled.")));
  }

  const postBoot$ = defer(() => {
    if (!closedWith) {
      try {
        worker.postMessage(bootRequest);
      } catch (error) {
        close(toError(error));
      }
    }

    return EMPTY;
  });

  return firstValueFrom(merge(ready$, closed$, postBoot$));
}
