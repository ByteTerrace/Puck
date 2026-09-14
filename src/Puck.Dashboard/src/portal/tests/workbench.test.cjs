// Renders `WorldWorkbench` — the studio's spatial surface, bound entirely to `StudioContext` (no
// props) — inside a `<StudioContext.Provider>` driven by a REAL `studioMachine` actor that has
// already booted against the real official tree and opened `games/tictactoe.world.json` (the same
// engine/official fixtures and `testBootEngine` stand-in `tests/studioMachine.test.cjs` uses — see
// its own header remarks on why `bootEngineFromOfficial` is not called directly here).
//
// The actor is advanced to a stable, invoke-free state (`ready.document.idle`) OUTSIDE React, then
// its `getPersistedSnapshot()` is handed to the Provider as `options.snapshot` — `createActor`
// hydrates a fresh actor at that exact snapshot synchronously (see `xstate`'s own `ActorOptions.snapshot`
// remarks), so a single synchronous `renderToStaticMarkup` pass already reflects the opened
// document with no need to wait on the Provider's own async boot invoke (which never runs under
// SSR anyway — `useEffect` does not fire in `renderToStaticMarkup`). `@xstate/react`'s `useSelector`
// reads `actor.getSnapshot()` for both its client AND server snapshot, so this is a supported,
// not an incidental, way to render a pre-advanced actor server-side.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');

const compilerOptions = { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX };
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions }).outputText, file);
require.extensions['.tsx'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions }).outputText, file);

const { pathToFileURL, fileURLToPath } = require('node:url');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MantineProvider } = require('@mantine/core');
const { createActor, waitFor } = require('xstate');

const { studioMachine } = require('../src/machines/studioMachine.ts');
const { resolveOfficial } = require('../src/official/officialBase.ts');
const { wrapRawExports } = require('../src/native/inlineHost.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');
const { StudioContext } = require('../src/context/StudioContext.tsx');
const { WorldWorkbench } = require('../src/components/world/WorldWorkbench.tsx');

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
const appBundleDir = path.join(root, 'src', 'Puck.World.Browser', 'bin', 'Release', 'net10.0', 'browser-wasm', 'AppBundle');
const mainMjs = path.join(appBundleDir, 'main.mjs');
const officialManifest = process.env.PUCK_TEST_OFFICIAL_MANIFEST || path.join(root, 'artifacts', 'official', 'dev', 'manifest.json');
const officialDir = path.dirname(path.dirname(officialManifest));
const officialChannel = path.basename(path.dirname(officialManifest));

const HAS_FIXTURES = fs.existsSync(mainMjs) && fs.existsSync(officialManifest);
assert.ok(!process.env.PUCK_TEST_OFFICIAL_MANIFEST || HAS_FIXTURES, 'Explicit release fixtures must exist');

if (!HAS_FIXTURES) {
  test(`WorldWorkbench (SKIPPED: no local AppBundle/official fixtures under ${appBundleDir} / ${officialDir} — ` +
    `publish Puck.World.Browser and run 'puck.exe official build' to produce them)`, { skip: true }, () => {});
} else {
  // Same disk-backed fetch stand-in as tests/studioMachine.test.cjs.
  function diskFetch(input) {
    const url = typeof input === 'string' ? input : input.href;
    let filePath;
    try {
      filePath = fileURLToPath(url);
    } catch {
      return Promise.resolve({ ok: false, status: 400, text: async () => '', arrayBuffer: async () => new ArrayBuffer(0) });
    }
    if (!fs.existsSync(filePath)) {
      return Promise.resolve({ ok: false, status: 404, text: async () => '', arrayBuffer: async () => new ArrayBuffer(0) });
    }
    return Promise.resolve({
      ok: true,
      status: 200,
      text: async () => fs.readFileSync(filePath, 'utf8'),
      arrayBuffer: async () => {
        const buffer = fs.readFileSync(filePath);
        return buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength);
      },
    });
  }

  function resolvedOfficial() {
    return resolveOfficial({
      VITE_PUCK_OFFICIAL_BASE: pathToFileURL(officialDir + path.sep).href,
      VITE_PUCK_OFFICIAL_CHANNEL: officialChannel,
    });
  }

  async function testBootEngine(officialLoad) {
    const module = await import(pathToFileURL(mainMjs).href);
    const raw = await module.createEngine();
    const engine = wrapRawExports(raw);
    const version = await engine.version();
    if (version.schemaVersion !== officialLoad.build.worldSchema || version.commit !== officialLoad.build.commit) {
      await engine.dispose();
      throw new Error(`engine build mismatch: ${version.schemaVersion}/${version.commit} vs official ${officialLoad.build.worldSchema}/${officialLoad.build.commit}`);
    }
    return engine;
  }

  function memoryStorage() {
    const entries = new Map();
    return { getItem: (key) => entries.get(key) ?? null, setItem: (key, value) => entries.set(key, value) };
  }

  function buildInput() {
    return {
      official: resolvedOfficial(),
      engineMode: 'inline',
      fetchImpl: diskFetch,
      bootEngine: testBootEngine,
      draftStore: new LocalDraftStore(memoryStorage()),
    };
  }

  /** Every component under test needs a MantineProvider ancestor. */
  function withMantine(element) {
    return React.createElement(MantineProvider, null, element);
  }

  let openedSnapshot;
  let topologyName;

  test('setup: boot a real actor and open games/tictactoe.world.json, outside React', async () => {
    const actor = createActor(studioMachine, { input: buildInput() });
    actor.start();
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });

    actor.send({ type: 'OPEN_OFFICIAL', name: 'games/tictactoe.world.json' });
    await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.deepEqual(actor.getSnapshot().context.document.diagnostics, []);

    topologyName = actor.getSnapshot().context.document.value.state.lattices[0].name;
    actor.send({ type: 'SELECT_TOPOLOGY', name: topologyName });
    // SELECT_TOPOLOGY is a synchronous assign in the machine's own `ready.on` handler — no invoke,
    // no need to waitFor; the snapshot reflects it the instant `.send` returns.
    assert.equal(actor.getSnapshot().context.selection.topology, topologyName);

    actor.send({ type: 'SELECT_CELLS', ordinals: [0], mode: 'replace' });
    assert.deepEqual(actor.getSnapshot().context.selection.ordinals, [0]);

    openedSnapshot = actor.getPersistedSnapshot();
    actor.stop();
  });

  test('WorldWorkbench renders the opened topology in the explorer and the selection count in the inspector', () => {
    const html = renderToStaticMarkup(withMantine(
      React.createElement(
        StudioContext.Provider,
        { options: { input: buildInput(), snapshot: openedSnapshot } },
        React.createElement(WorldWorkbench),
      ),
    ));

    assert.ok(html.includes('Document explorer'));
    assert.ok(html.includes(topologyName), `expected the explorer to list the opened topology '${topologyName}'`);
    assert.ok(html.includes('aria-pressed="true"'), 'the opened topology is shown selected in the explorer');
    assert.ok(html.includes('1 cell selected'), 'the inspector reflects the one pre-selected cell');
    assert.ok(html.includes('Paint authored cells'));
    assert.ok(html.includes('Value appearance'));
  });

  test('WorldWorkbench with no selection shows the inspector\'s empty-selection prompt', () => {
    const actor = createActor(studioMachine, { input: buildInput(), snapshot: openedSnapshot });
    // Clear the selection this fresh actor was hydrated with, then re-capture — a fresh actor
    // hydrated from a snapshot never auto-starts its own invokes for an idle state, so this stays synchronous.
    actor.start();
    actor.send({ type: 'SELECT_CELLS', ordinals: [], mode: 'replace' });
    const clearedSnapshot = actor.getPersistedSnapshot();
    actor.stop();

    const html = renderToStaticMarkup(withMantine(
      React.createElement(
        StudioContext.Provider,
        { options: { input: buildInput(), snapshot: clearedSnapshot } },
        React.createElement(WorldWorkbench),
      ),
    ));
    assert.ok(html.includes('Select a cell'));
    assert.ok(!html.includes('1 cell selected'));
  });
}
