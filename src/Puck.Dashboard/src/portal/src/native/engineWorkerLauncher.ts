// Isolate import.meta.url from the transport, which is also exercised under Node.
import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest } from "./engineHost";
import { connectWorkerEngine } from "./workerClient";

/** Creates a dedicated worker and waits for its engine boot handshake. */
export function createWorkerEngine(bootRequest: WorkerRequest, signal?: AbortSignal): Promise<WorldEngine> {
  signal?.throwIfAborted();
  return connectWorkerEngine(new Worker(new URL("./engine.worker.ts", import.meta.url), { type: "module" }), bootRequest, signal);
}
