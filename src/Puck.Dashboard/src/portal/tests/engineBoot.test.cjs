// Proves the official-tree boot path (engineBoot.ts/workerBoot.ts) over a REAL served official
// tree — fetched through actual HTTP, never the filesystem, because that is exactly what proves
// the resourceLoader path (a content-addressed object URL has no "_framework/" directory of its
// own to relatively fetch against; see workerBoot.ts's own remarks). The server is the published
// `puck official serve` binary itself, run as a child process, so the wire format under test is
// the real one `puck official build` produces — not a hand-rolled stand-in.
const assert = require('node:assert/strict');
const { test, after } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawn } = require('node:child_process');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { resolveOfficial } = require('../src/official/officialBase.ts');
const { loadOfficial } = require('../src/official/officialClient.ts');
const { createByteStore } = require('../src/official/byteStore.ts');
const { OfficialRefusal } = require('../src/official/verify.ts');
const { bootEngineFromOfficial, bootEngineFromLocalBundle } = require('../src/native/engineBoot.ts');
const { bootEngineFromOfficialFiles } = require('../src/native/workerBoot.ts');

function repositoryRoot() {
  let dir = __dirname;
  while (true) {
    if (fs.existsSync(path.join(dir, 'Puck.slnx'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error('could not find Puck.slnx walking up from ' + __dirname);
    dir = parent;
  }
}

const root = repositoryRoot();
const officialTreeDir = path.join(root, 'artifacts', 'official');
const pckExe = path.join(root, 'src', 'Puck.Cli', 'publish', 'puck.exe');
const appBundleDir = path.join(root, 'src', 'Puck.World.Browser', 'bin', 'Release', 'net10.0', 'browser-wasm', 'AppBundle');
const CHANNEL = 'dev';

const ready = fs.existsSync(path.join(officialTreeDir, CHANNEL, 'manifest.json')) && fs.existsSync(pckExe);

if (!ready) {
  test(`engineBoot (SKIPPED: no official tree at ${officialTreeDir} or no puck.exe at ${pckExe} — run 'puck.exe official build' and publish Puck.Cli)`, { skip: true }, () => {});
} else {
  const servers = [];

  function copyTree(from, to) {
    fs.mkdirSync(to, { recursive: true });
    fs.cpSync(from, to, { recursive: true });
  }

  /** Starts `puck official serve` over `treeDir` on `port` and resolves once it reports ready. */
  function startServer(treeDir, port) {
    return new Promise((resolve, reject) => {
      const proc = spawn(pckExe, ['official', 'serve', '--tree', treeDir, '--port', String(port)], { stdio: ['ignore', 'pipe', 'pipe'] });
      let settled = false;
      const onData = (chunk) => {
        if (settled) return;
        if (chunk.toString().includes('serving')) {
          settled = true;
          proc.stdout.off('data', onData);
          resolve({ proc, baseUrl: `http://localhost:${port}` });
        }
      };
      proc.stdout.on('data', onData);
      proc.stderr.on('data', (chunk) => { if (!settled) reject(new Error(`puck official serve stderr: ${chunk}`)); });
      proc.on('error', reject);
      proc.on('exit', (code) => { if (!settled) reject(new Error(`puck official serve exited early with code ${code}`)); });
      servers.push(proc);
    });
  }

  after(() => {
    for (const proc of servers) {
      try { proc.kill(); } catch { /* already gone */ }
    }
  });

  function officialFor(baseUrl) {
    return resolveOfficial({ VITE_PUCK_OFFICIAL_BASE: baseUrl, VITE_PUCK_OFFICIAL_CHANNEL: CHANNEL });
  }

  test('bootEngineFromOfficial (inline) reaches version() with the manifest\'s own commit', async () => {
    const { baseUrl } = await startServer(officialTreeDir, 61301);
    const official = await loadOfficial(officialFor(baseUrl), fetch, createByteStore());

    const engine = await bootEngineFromOfficial(official, { mode: 'inline' });
    try {
      const version = await engine.version();
      assert.equal(version.schemaVersion, official.build.worldSchema);
      assert.equal(version.commit, official.build.commit);
      assert.equal(version.engine, 'Puck.World.Browser');
    } finally {
      await engine.dispose();
    }
  });

  test('a manifest whose build.commit disagrees with the running engine is refused by name', async () => {
    const { baseUrl } = await startServer(officialTreeDir, 61302);
    const resolved = officialFor(baseUrl);

    // Tamper only the manifest text handed to loadOfficial; every engine object is fetched from
    // the real, untouched server, so the booted engine reports its own REAL commit.
    const tamperingFetch = async (input) => {
      const url = typeof input === 'string' ? input : input.href;
      if (url === resolved.manifestUrl.href) {
        const real = await fetch(url);
        const manifest = await real.json();
        manifest.build = { ...manifest.build, commit: 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef' };
        return new Response(JSON.stringify(manifest), { status: 200, headers: { 'content-type': 'application/json' } });
      }
      return fetch(input);
    };

    const official = await loadOfficial(resolved, tamperingFetch, createByteStore());
    assert.equal(official.build.commit, 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef');

    await assert.rejects(bootEngineFromOfficial(official, { mode: 'inline' }), (error) => {
      assert.ok(error instanceof OfficialRefusal, error.stack);
      // Names both the tampered manifest commit and the real engine's own reported commit.
      assert.match(error.message, /deadbeefdeadbeefdeadbeefdeadbeefdeadbeef/);
      assert.match(error.message, /schemaVersion/);
      return true;
    });
  });

  test('a tampered engine object is refused by name before the engine runs', async () => {
    const tamperedTreeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'puck-official-tampered-'));
    copyTree(officialTreeDir, tamperedTreeDir);

    const manifest = JSON.parse(fs.readFileSync(path.join(tamperedTreeDir, CHANNEL, 'manifest.json'), 'utf8'));
    const stateEntry = manifest.engine.files.find((f) => f.name === '_framework/Puck.State.wasm');
    assert.ok(stateEntry, 'manifest names no _framework/Puck.State.wasm engine file');
    const objectPath = path.join(tamperedTreeDir, ...stateEntry.path.split('/'));
    const bytes = fs.readFileSync(objectPath);
    bytes[0] = bytes[0] ^ 0xff;
    fs.writeFileSync(objectPath, bytes);

    const { baseUrl } = await startServer(tamperedTreeDir, 61303);
    const official = await loadOfficial(officialFor(baseUrl), fetch, createByteStore());

    let engineRan = false;
    await assert.rejects(
      bootEngineFromOfficialFiles({ engineFiles: official.engineFiles }, fetch, createByteStore()).then((engine) => {
        engineRan = true;
        return engine;
      }),
      (error) => {
        assert.ok(error instanceof OfficialRefusal, error.stack);
        assert.match(error.message, /Puck\.State\.wasm/);
        assert.equal(error.expectedHash, stateEntry.hash);
        assert.notEqual(error.actualHash, stateEntry.hash);
        return true;
      },
    );
    assert.equal(engineRan, false, 'the engine must never run once one of its objects fails verification');
  });

  test('the byte store is warm after the first boot: a second boot makes zero network fetches', async () => {
    const { baseUrl } = await startServer(officialTreeDir, 61304);
    const official = await loadOfficial(officialFor(baseUrl), fetch, createByteStore());
    const store = createByteStore();

    let fetchCount = 0;
    const countingFetch = (input) => {
      fetchCount += 1;
      return fetch(input);
    };

    const first = await bootEngineFromOfficialFiles({ engineFiles: official.engineFiles }, countingFetch, store);
    const firstCount = fetchCount;
    assert.ok(firstCount > 0, 'the first boot must fetch every engine file at least once');
    await first.dispose();

    fetchCount = 0;
    const second = await bootEngineFromOfficialFiles({ engineFiles: official.engineFiles }, countingFetch, store);
    assert.equal(fetchCount, 0, 'a warm byte store must answer the second boot with zero fetchImpl calls');
    const version = await second.version();
    assert.equal(version.commit, official.build.commit);
    await second.dispose();
  });

  test('worker-mode\'s pure core (workerBoot.ts) boots and refuses through a fake postMessage pair, exercising engine.worker.ts itself', async () => {
    const { baseUrl } = await startServer(officialTreeDir, 61305);
    const official = await loadOfficial(officialFor(baseUrl), fetch, createByteStore());

    // engine.worker.ts reads `self`/`postMessage` from the global scope - fake both so its own
    // `self.onmessage = ...` top-level assignment attaches here, then drive it exactly as
    // engineBoot.ts's real Worker construction would (a MessageEvent-shaped object in, a captured
    // postMessage call out). This is engine.worker.ts's OWN file under test, not a re-implementation.
    const posted = [];
    const fakeSelf = { onmessage: null, close: () => {} };
    global.self = fakeSelf;
    global.postMessage = (message) => posted.push(message);
    try {
      delete require.cache[require.resolve('../src/native/engine.worker.ts')];
      require('../src/native/engine.worker.ts');
      assert.equal(typeof fakeSelf.onmessage, 'function');

      await fakeSelf.onmessage({ data: { kind: 'boot', engineFiles: official.engineFiles } });
      assert.equal(posted.length, 1);
      assert.equal(posted[0].kind, 'ready');

      // A second 'boot' over a fresh corrupted copy, through the SAME worker module instance,
      // proves the refusal path posts 'init-error' naming the corrupted file.
      const tamperedTreeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'puck-official-worker-tampered-'));
      copyTree(officialTreeDir, tamperedTreeDir);
      const manifest = JSON.parse(fs.readFileSync(path.join(tamperedTreeDir, CHANNEL, 'manifest.json'), 'utf8'));
      const abstractionsEntry = manifest.engine.files.find((f) => f.name === '_framework/Puck.Abstractions.wasm');
      const objectPath = path.join(tamperedTreeDir, ...abstractionsEntry.path.split('/'));
      const bytes = fs.readFileSync(objectPath);
      bytes[0] = bytes[0] ^ 0xff;
      fs.writeFileSync(objectPath, bytes);
      const { baseUrl: tamperedBaseUrl } = await startServer(tamperedTreeDir, 61306);
      const tamperedOfficial = await loadOfficial(officialFor(tamperedBaseUrl), fetch, createByteStore());

      // workerBoot.ts's default byte store is a module-level singleton (one warm cache per realm
      // - see its own remarks): the first 'boot' above already cached Puck.Abstractions.wasm's
      // CORRECT bytes under its (unchanged) hash, which would silently mask this corruption. A
      // real Worker gets a fresh module graph per instance; clearing the require cache for both
      // engine.worker.ts and workerBoot.ts before re-requiring reproduces that same fresh state.
      delete require.cache[require.resolve('../src/native/engine.worker.ts')];
      delete require.cache[require.resolve('../src/native/workerBoot.ts')];
      require('../src/native/engine.worker.ts');

      posted.length = 0;
      await fakeSelf.onmessage({ data: { kind: 'boot', engineFiles: tamperedOfficial.engineFiles } });
      assert.equal(posted.length, 1);
      assert.equal(posted[0].kind, 'init-error');
      assert.match(posted[0].error, /Puck\.Abstractions\.wasm/);
    } finally {
      delete global.self;
      delete global.postMessage;
    }
  });

  test('bootEngineFromLocalBundle boots a plain AppBundle directory with no official manifest', { skip: !fs.existsSync(path.join(appBundleDir, 'main.mjs')) }, async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const version = await engine.version();
      assert.equal(version.engine, 'Puck.World.Browser');
    } finally {
      await engine.dispose();
    }
  });
}
