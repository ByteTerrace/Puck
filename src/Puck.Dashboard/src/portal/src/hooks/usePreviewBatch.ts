import { useCallback, useEffect, useRef, useState } from "react";
import batchWorkerUrl from "../engine/previewBatch.worker?worker&url";

/** A cancellable worker per batch, including a bootstrap for federated portal origins. */
export function usePreviewBatch<T>() {
  const active = useRef<{ worker: Worker; timer: ReturnType<typeof setTimeout>; bootstrap?: string } | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<T | null>(null);
  const dispose = useCallback(() => {
    if(!active.current) return;
    active.current.worker.terminate();
    clearTimeout(active.current.timer);
    if(active.current.bootstrap) URL.revokeObjectURL(active.current.bootstrap);
    active.current = null;
  }, []);
  const stop = useCallback(() => { dispose(); setRunning(false); }, [dispose]);
  useEffect(() => dispose, [dispose]);
  const run = useCallback((payload: unknown) => {
    stop(); setError(null); setResult(null); setRunning(true);
    let bootstrap: string | undefined;
    try {
      const source = new URL(batchWorkerUrl, import.meta.url);
      // Worker entry scripts must share the page origin. Module imports may use CORS.
      if(source.origin !== location.origin) {
        bootstrap = URL.createObjectURL(new Blob(["import " + JSON.stringify(source.href) + ";"], { type: "text/javascript" }));
      }
      const worker = new Worker(bootstrap ?? source, { type: "module" });
      const timer = setTimeout(() => {
        stop(); setError("Preview batch exceeded 30 seconds. Try fewer games or simpler rules.");
      }, 30000);
      active.current = { worker, timer, bootstrap };
      worker.onmessage = ({ data }) => {
        if(active.current?.worker !== worker) return;
        stop();
        if(data.error) setError(data.error); else setResult(data.result);
      };
      worker.onerror = () => {
        if(active.current?.worker !== worker) return;
        stop(); setError("Preview worker failed. Check the document and try again.");
      };
      worker.postMessage(payload);
    } catch(error) {
      stop();
      if(bootstrap) URL.revokeObjectURL(bootstrap);
      setError((error as Error).message);
    }
  }, [stop]);
  return { run, stop, running, error, result };
}
