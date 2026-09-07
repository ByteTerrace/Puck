// The `'worker'` engine host's WORKER-side script: boots Puck.World.Browser's main.mjs inside this module Worker's
// own global scope and reuses inlineHost's wire-decoding (`wrapRawExports`) so the wasm engine and its wrapping run
// entirely off the main thread — the two hosting modes decode the SAME wire shapes from exactly one place. Node has
// no real Worker/DOM — `engineHost.ts`/`engineWorkerLauncher.ts` reach this file only through
// `new Worker(new URL("./engine.worker.ts", ...))`, a construction that never runs under Node — but
// tests/engineBoot.test.cjs still requires THIS file directly under Node, faking `self`/`postMessage` on the
// global scope so its own `self.onmessage = ...` assignment attaches there, then drives it exactly as a real
// Worker's postMessage would (see that test's own remarks).
//
// `'boot'` requests (engineBoot.ts's official-tree boot path) are handled by delegating to
// `workerBoot.bootEngineFromOfficialFiles` — a pure module also driven directly under Node with a
// fake postMessage pair (tests/engineBoot.test.cjs), so this file's own `'boot'` case is a thin
// shell over logic already proven without a real Worker.
import { wrapRawExports, dynamicImport, type CreateRawEngine } from "./inlineHost";
import { bootEngineFromOfficialFiles } from "./workerBoot";
import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest, WorkerResponse } from "./engineHost";

let engine: WorldEngine | null = null;

self.onmessage = async (event: MessageEvent<WorkerRequest>) => {
  const request = event.data;

  switch (request.kind) {
    case "init": {
      try {
        const module = (await dynamicImport(request.engineEntryUrl)) as { createEngine: CreateRawEngine };
        const raw = await module.createEngine();

        engine = wrapRawExports(raw, () => self.close());
        postMessage({ kind: "ready" } satisfies WorkerResponse);
      } catch (error) {
        postMessage({ kind: "init-error", error: String(error) } satisfies WorkerResponse);
      }

      return;
    }
    case "boot": {
      try {
        // No fetchImpl crosses here (it cannot); the worker's own global `fetch` and byte store
        // do the fetching and hash-verifying entirely on this side of the boundary.
        engine = await bootEngineFromOfficialFiles({ engineFiles: request.engineFiles }, fetch, undefined, () => self.close());
        postMessage({ kind: "ready" } satisfies WorkerResponse);
      } catch (error) {
        postMessage({ kind: "init-error", error: String(error) } satisfies WorkerResponse);
      }

      return;
    }
    case "dispose": {
      await engine?.dispose();

      return;
    }
    case "call": {
      if (!engine) {
        postMessage({ kind: "result", id: request.id, ok: false, error: "engine.worker: a call arrived before 'init' finished." } satisfies WorkerResponse);

        return;
      }

      try {
        const method = engine[request.method] as (...args: unknown[]) => Promise<unknown>;
        const value = await method.apply(engine, request.args);

        postMessage({ kind: "result", id: request.id, ok: true, value } satisfies WorkerResponse);
      } catch (error) {
        postMessage({ kind: "result", id: request.id, ok: false, error: String(error) } satisfies WorkerResponse);
      }
    }
  }
};
