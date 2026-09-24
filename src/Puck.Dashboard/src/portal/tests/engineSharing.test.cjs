// The engine's 30 MB wasm module is compiled once per session: the first engine compiles it, and the second boots
// from that compiled module and only instantiates it. Each boot runs on its own Node thread, as each engine runs in
// its own browser worker, and the proof is a count of WebAssembly calls per thread, never a timing.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { Worker } = require('node:worker_threads');
require('./support/register.cjs');
const { bootEngineFromOfficialFiles, engineWasmResponse, integrityFor } = require('../src/native/workerBoot.ts');

const bundle = path.resolve(__dirname, '../../../../Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle');

test('an integrity value is the manifest hash in SRI form', () => {
  const bytes = Buffer.from('engine bytes');
  const hex = crypto.createHash('sha256').update(bytes).digest('hex');
  assert.equal(integrityFor(`sha256/${hex}`), `sha256-${crypto.createHash('sha256').update(bytes).digest('base64')}`);
});

// The browser path of the wasm response, driven with doubles: a fetch that ignores `integrity`, and a byte store.
// The smallest valid module stands in for the engine's wasm; a second valid module stands in for tampered bytes.
const WASM = new Uint8Array([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]);
const TAMPERED = new Uint8Array([...WASM, 0x00, 0x04, 0x03, 0x78, 0x79, 0x7a]);
const REF = { url: 'https://official.test/off/objects/sha256/ab/wasm', hash: `sha256/${crypto.createHash('sha256').update(WASM).digest('hex')}` };
const NAME = '_framework/dotnet.native.wasm';

function memoryStore(entries = new Map()) {
  return {
    entries,
    has: async (key) => entries.has(key),
    get: async (key) => entries.get(key),
    put: async (key, bytes) => { entries.set(key, bytes); },
  };
}
const serving = (bytes) => async () => new Response(bytes.slice().buffer, { headers: { 'content-type': 'application/wasm' } });
const offline = async () => { throw new Error('offline'); };

test('fetched wasm bytes are checked against the manifest before they compile or are stored', async () => {
  const store = memoryStore();
  let compiled = null;
  const good = await engineWasmResponse(NAME, REF, serving(WASM), store, undefined, undefined, (module) => { compiled = module; });
  assert.ok((await WebAssembly.compileStreaming(good)) instanceof WebAssembly.Module);
  assert.ok(compiled instanceof WebAssembly.Module, 'the module is reported back');
  assert.deepEqual([...store.entries.get(REF.hash)], [...WASM], 'the verified bytes are stored');

  const empty = memoryStore();
  const tampered = await engineWasmResponse(NAME, REF, serving(TAMPERED), empty, undefined, undefined, () => {});
  await assert.rejects(WebAssembly.compileStreaming(tampered), (error) => {
    assert.equal(error.name, 'OfficialRefusal');
    assert.equal(error.objectPath, NAME);
    assert.equal(error.expectedHash, REF.hash);
    return true;
  });
  assert.equal(empty.entries.size, 0, 'bytes nobody verified are never stored');
});

test('a stored wasm copy is verified before it answers for a failed fetch', async () => {
  const good = await engineWasmResponse(NAME, REF, offline, memoryStore(new Map([[REF.hash, WASM]])), undefined, undefined, () => {});
  assert.ok((await WebAssembly.compileStreaming(good)) instanceof WebAssembly.Module);

  await assert.rejects(
    engineWasmResponse(NAME, REF, offline, memoryStore(new Map([[REF.hash, TAMPERED]])), undefined, undefined, () => {}),
    (error) => error.name === 'OfficialRefusal' && error.objectPath === NAME && error.expectedHash === REF.hash,
  );
  await assert.rejects(
    engineWasmResponse(NAME, REF, offline, memoryStore(), undefined, undefined, () => {}),
    /no verified copy is cached/,
  );
});

test('an engine file whose stored copy is tampered is refused by name before the engine runs', async () => {
  const script = new TextEncoder().encode('export const dotnet = {};\n');
  const ref = (bytes, url) => ({ url, hash: `sha256/${crypto.createHash('sha256').update(bytes).digest('hex')}` });
  const files = {
    '_framework/dotnet.js': ref(script, 'https://official.test/off/objects/sha256/aa/dotnet'),
    '_framework/dotnet.native.wasm': ref(WASM, 'https://official.test/off/objects/sha256/bb/wasm'),
  };
  const store = memoryStore(new Map([
    [files['_framework/dotnet.js'].hash, new TextEncoder().encode('tampered in the store')],
    [files['_framework/dotnet.native.wasm'].hash, WASM],
  ]));
  await assert.rejects(bootEngineFromOfficialFiles({ engineFiles: files }, offline, store), (error) => {
    assert.equal(error.name, 'OfficialRefusal');
    assert.equal(error.objectPath, '_framework/dotnet.js');
    assert.equal(error.expectedHash, files['_framework/dotnet.js'].hash);
    return true;
  });
});

if (!fs.existsSync(path.join(bundle, 'main.mjs'))) {
  test(`engineSharing (SKIPPED: no AppBundle at ${bundle} — run 'dotnet publish src/Puck.World.Browser -c Release')`, { skip: true }, () => {});
} else {
  /** Every AppBundle file as the official manifest names it: by its bundle-relative name, with a URL and a hash. */
  function engineFiles() {
    const files = {};
    const walk = (directory) => {
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        const full = path.join(directory, entry.name);
        if (entry.isDirectory()) { walk(full); continue; }
        const hash = crypto.createHash('sha256').update(fs.readFileSync(full)).digest('hex');
        files[path.relative(bundle, full).split(path.sep).join('/')] = { url: pathToFileURL(full).href, hash: `sha256/${hash}` };
      }
    };
    walk(bundle);
    return files;
  }

  function bootOnThread(workerData) {
    return new Promise((resolve, reject) => {
      const worker = new Worker(path.join(__dirname, 'support', 'engineThread.cjs'), { workerData });
      worker.once('message', (message) => { resolve(message); void worker.terminate(); });
      worker.once('error', reject);
    });
  }

  const compiles = (counts) => counts.compile + counts.compileStreaming;

  test('the first engine compiles the module once; the second instantiates it and compiles nothing', { timeout: 300_000 }, async () => {
    const files = engineFiles();
    const language = await bootOnThread({ engineFiles: files });
    assert.equal(language.ok, true, language.error);
    assert.equal(compiles(language.counts), 1, JSON.stringify(language.counts));
    assert.ok(language.wasmModule instanceof WebAssembly.Module, 'the compiled module comes back with the engine');

    const world = await bootOnThread({ engineFiles: files, wasmModule: language.wasmModule });
    assert.equal(world.ok, true, world.error);
    assert.equal(compiles(world.counts), 0, JSON.stringify(world.counts));
    assert.equal(world.counts.instantiate, 1, JSON.stringify(world.counts));
    assert.deepEqual(world.version, language.version, 'both threads run the same engine build');
  });

  test('a wasm whose bytes disagree with the manifest hash is refused by name', { timeout: 300_000 }, async () => {
    const files = engineFiles();
    const wasm = Object.keys(files).find((name) => name.endsWith('dotnet.native.wasm'));
    files[wasm] = { ...files[wasm], hash: `sha256/${'0'.repeat(64)}` };
    const refused = await bootOnThread({ engineFiles: files });
    assert.equal(refused.ok, false);
    assert.match(refused.error, /OfficialRefusal/);
    assert.match(refused.error, /dotnet\.native\.wasm/);
  });
}
