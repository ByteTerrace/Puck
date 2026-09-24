/**
 * How many times this JS realm compiled or instantiated WebAssembly, counted by wrapping the global calls. The studio
 * compiles the engine's 30 MB module once per session and hands the compiled module to its second engine, which
 * only instantiates it; these counts are how a test proves that without a clock. The worker installs the wrappers
 * before the engine loads; a Node test installs them in its own realm.
 */

export interface WasmCallCounts {
  compile: number;
  compileStreaming: number;
  instantiate: number;
  instantiateStreaming: number;
}

const counts: WasmCallCounts = { compile: 0, compileStreaming: 0, instantiate: 0, instantiateStreaming: 0 };
let installed = false;

/** Wraps `WebAssembly.compile`, `compileStreaming`, `instantiate`, and `instantiateStreaming` with counters, once
 * per realm. Each wrapper calls the original unchanged. */
export function installWasmCallCounts(): void {
  if (installed) return;
  installed = true;
  const wasm = WebAssembly as unknown as Record<keyof WasmCallCounts, ((...args: unknown[]) => unknown) | undefined>;
  for (const name of Object.keys(counts) as (keyof WasmCallCounts)[]) {
    const original = wasm[name];
    if (typeof original !== "function") continue;
    wasm[name] = function (this: unknown, ...args: unknown[]) {
      counts[name]++;
      return original.apply(this, args);
    };
  }
}

/** The counts so far in this realm (all zero when the wrappers were never installed). */
export function wasmCallCounts(): WasmCallCounts {
  return { ...counts };
}
