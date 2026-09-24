// Isolate import.meta.url from the transport, which is also exercised under Node.
import type { WorldEngine } from "./engineTypes";
import type { WorkerRequest } from "./engineHost";
import engineWorkerUrl from "./engine.worker.ts?worker&url";
import { connectWorkerEngine } from "./workerClient";

/**
 * A module worker for `url`. Served on its own origin, or under the host's `/portal` path in production, the
 * portal constructs the worker directly. Federated into a host on another origin (local development, where the
 * host and portal run on separate ports), the browser refuses a cross-origin worker script, so a same-origin
 * module blob imports it instead; the portal's dev server answers that import with CORS.
 */
function moduleWorker(url: URL): Worker {
  if (url.origin === location.origin) {
    return new Worker(url, { type: "module" });
  }

  const bootstrap = new Blob([`import ${JSON.stringify(url.href)};`], { type: "text/javascript" });

  return new Worker(URL.createObjectURL(bootstrap), { type: "module" });
}

/** Creates a dedicated worker and waits for its engine boot handshake. */
export function createWorkerEngine(bootRequest: WorkerRequest, signal?: AbortSignal): Promise<WorldEngine> {
  signal?.throwIfAborted();
  return connectWorkerEngine(moduleWorker(new URL(engineWorkerUrl, import.meta.url)), bootRequest, signal);
}
