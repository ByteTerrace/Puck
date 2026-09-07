// The `'worker'` engine host's WORKER-side script: boots Puck.World.Browser's main.mjs inside this module Worker's
// own global scope and reuses inlineHost's wire-decoding (`wrapRawExports`) so the wasm engine and its wrapping run
// entirely off the main thread — the two hosting modes decode the SAME wire shapes from exactly one place. Node has
// no Worker/DOM, so this file is never imported by a test; `engineHost.ts` reaches it only through
// `new Worker(new URL("./engine.worker.ts", ...))`, a construction `'inline'` mode never executes.
import { wrapRawExports, type CreateRawEngine } from "./inlineHost";
import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest, WorkerResponse } from "./engineHost";

let engine: WorldEngine | null = null;

self.onmessage = async (event: MessageEvent<WorkerRequest>) => {
  const request = event.data;

  switch (request.kind) {
    case "init": {
      try {
        const module = (await import(/* @vite-ignore */ request.engineEntryUrl)) as { createEngine: CreateRawEngine };
        const raw = await module.createEngine();

        engine = wrapRawExports(raw, () => self.close());
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
