// One engine boot on its own Node thread, the way a browser worker boots: from engine files named by URL and hash,
// through workerBoot's official boot path, counting every WebAssembly compile and instantiation this thread makes.
// It posts back the counts, the engine's version, and the compiled module it ran, which the next thread can take.
require('./register.cjs');
const fs = require('node:fs');
const { fileURLToPath } = require('node:url');
const { parentPort, workerData } = require('node:worker_threads');
const { installWasmCallCounts, wasmCallCounts } = require('../../src/native/wasmCounts.ts');
const { bootEngineFromOfficialFiles } = require('../../src/native/workerBoot.ts');

installWasmCallCounts();

function diskFetch(input) {
  const path = fileURLToPath(typeof input === 'string' ? input : input.href);
  if (!fs.existsSync(path)) return Promise.resolve({ ok: false, status: 404, arrayBuffer: async () => new ArrayBuffer(0) });
  const bytes = fs.readFileSync(path);
  return Promise.resolve({ ok: true, status: 200, arrayBuffer: async () => bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) });
}

(async () => {
  try {
    const engine = await bootEngineFromOfficialFiles({ engineFiles: workerData.engineFiles, wasmModule: workerData.wasmModule }, diskFetch);
    const version = await engine.version();
    parentPort.postMessage({ ok: true, counts: wasmCallCounts(), version, wasmModule: engine.wasmModule });
  } catch (error) {
    parentPort.postMessage({ ok: false, error: `${error.name}: ${error.message}` });
  }
})();
