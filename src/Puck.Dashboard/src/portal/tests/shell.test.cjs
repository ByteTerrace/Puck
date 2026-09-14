// The composed studio shell (`StudioShell.tsx` — header, tabs, alerts, drafts panel), driven by a
// REAL `studioMachine` actor over the real official tree and AppBundle, the same fixtures and
// `testBootEngine`/`diskFetch` stand-ins `tests/studioMachine.test.cjs` and `tests/workbench.test.cjs`
// already use. `StudioShell.tsx` itself never reads `import.meta.env` (that lives only in
// `WorldStudio.tsx`, deliberately outside every test's require graph — see its own header remarks),
// so this file builds `StudioMachineInput` by hand and constructs `StudioContext.Provider` directly,
// exactly as `tests/workbench.test.cjs` does for `WorldWorkbench`.
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
const { MAX_DOCUMENT_BYTES } = require('../src/document/intake.ts');
const { StudioContext } = require('../src/context/StudioContext.tsx');
const { StudioShellWithConfirmation, PreviewTab } = require('../src/components/world/StudioShell.tsx');
const { DraftsPanel } = require('../src/components/world/drafts/DraftsPanel.tsx');
const { WorldStudioAlerts } = require('../src/components/world/WorldStudioAlerts.tsx');

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
  test(`StudioShell (SKIPPED: no local AppBundle/official fixtures under ${appBundleDir} / ${officialDir} — ` +
    `publish Puck.World.Browser and run 'puck.exe official build' to produce them)`, { skip: true }, () => {});
} else {
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

  function buildInput(draftStore) {
    return {
      official: resolvedOfficial(),
      engineMode: 'inline',
      fetchImpl: diskFetch,
      bootEngine: testBootEngine,
      draftStore: draftStore ?? new LocalDraftStore(memoryStorage()),
    };
  }

  function withMantine(element) {
    return React.createElement(MantineProvider, null, element);
  }

  function renderWithSnapshot(component, input, snapshot) {
    return renderToStaticMarkup(withMantine(
      React.createElement(StudioContext.Provider, { options: { input, snapshot } }, component),
    ));
  }

  let openedSnapshot;
  let boot;

  test('setup: boot a real actor and open games/tictactoe.world.json, outside React', async () => {
    const actor = createActor(studioMachine, { input: buildInput() });
    actor.start();
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });
    boot = actor.getSnapshot().context.boot;
    assert.equal(boot.status, 'ready');

    actor.send({ type: 'OPEN_OFFICIAL', name: 'games/tictactoe.world.json' });
    await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.validation === 'clean', { timeout: 120_000 });
    assert.deepEqual(actor.getSnapshot().context.document.diagnostics, []);

    openedSnapshot = actor.getPersistedSnapshot();
    actor.stop();
  });

  test('StudioShell header shows the opened document\'s role badge and the official build commit', () => {
    const html = renderWithSnapshot(React.createElement(StudioShellWithConfirmation), buildInput(), openedSnapshot);
    assert.ok(html.includes('games/tictactoe.world.json'), 'the open document\'s name is shown');
    assert.ok(html.includes('>fragment<'), 'the role badge names the fragment role');
    assert.ok(html.includes(boot.build.commit.slice(0, 12)), 'the build commit is shown');
  });

  test('StudioShell\'s default Sections tab lists the document\'s root sections', () => {
    const html = renderWithSnapshot(React.createElement(StudioShellWithConfirmation), buildInput(), openedSnapshot);
    const explorerItems = html.match(/studio-explorer-item/g) ?? [];
    assert.ok(explorerItems.length > 1, `expected multiple root sections listed, found ${explorerItems.length}`);
    assert.ok(html.includes('Extensions'), 'the root extensions bag is offered as its own entry');
  });

  test('PreviewTab shows the machine\'s own refusal text before a preview ever starts', async () => {
    const actor = createActor(studioMachine, { input: buildInput() });
    actor.start();
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });
    actor.send({ type: 'PREVIEW_START' });
    await waitFor(actor, (s) => s.matches({ ready: { preview: 'refused' } }), { timeout: 5_000 });
    const snapshot = actor.getPersistedSnapshot();
    actor.stop();

    const html = renderWithSnapshot(React.createElement(PreviewTab), buildInput(), snapshot);
    assert.ok(html.includes('unresolved diagnostics'), 'the refusal text is shown before any preview starts');
  });

  test('PreviewTab renders the rule trace after a start + tick', async () => {
    // A real fragment's PREVIEW_START compiles the WHOLE composed island (hundreds of KB), which
    // `tests/studioMachine.test.cjs`'s own preview-mechanics tests document as taking upward of
    // ten seconds even warm — preview mechanics themselves are state-machine plumbing, not island
    // content, so this exercises them (through the same real engine/official fixtures) over a
    // small standalone document instead, exactly the pattern that file's own `previewActor`
    // fixture establishes, rather than reproducing the full-island cost here.
    const actor = createActor(studioMachine, { input: buildInput() });
    actor.start();
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });
    actor.send({
      type: 'OPEN_TEXT',
      name: 'preview-fixture.world.json',
      text: JSON.stringify({
        schema: 'puck.world.def.v1',
        documentId: 'shell-preview-fixture',
        state: { world: [{ name: 'counter', kind: 'Int', value: 0 }] },
      }),
    });
    await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.validation === 'clean', { timeout: 120_000 });

    actor.send({ type: 'PREVIEW_START' });
    await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 120_000 });
    actor.send({ type: 'PREVIEW_TICK' });
    await waitFor(actor, (s) => s.context.preview.snapshots.length > 1, { timeout: 120_000 });

    const snapshot = actor.getPersistedSnapshot();
    actor.stop();

    const html = renderWithSnapshot(React.createElement(PreviewTab), buildInput(), snapshot);
    assert.ok(html.includes('Rules visited'), 'the rule trace view rendered');
    assert.ok(html.includes('Writes'), 'the rule trace view rendered its writes section');
  });

  test('DraftsPanel lists a saved local draft', () => {
    // `machineInput.draftStore` is fixed into `context` by the machine's own context factory the
    // instant an actor is constructed from `input` — no snapshot/boot wait needed to read it back,
    // so this deliberately passes no `snapshot` (a fresh actor, still in `booting`) rather than
    // reusing `openedSnapshot`, whose OWN context already froze a different (empty) draft store.
    const draftStore = new LocalDraftStore(memoryStorage());
    draftStore.save('demo-draft', 'Demo draft', 'games/tictactoe.world.json', '{"documentId":"demo"}', 'seed');
    const input = buildInput(draftStore);

    const html = renderWithSnapshot(React.createElement(DraftsPanel), input, undefined);
    assert.ok(html.includes('Demo draft'), 'the saved draft\'s title is listed');
    assert.ok(html.includes('games/tictactoe.world.json'), 'the draft\'s source document name is listed');
  });

  test('an oversize imported document is refused by message, surfaced through WorldStudioAlerts', async () => {
    const actor = createActor(studioMachine, { input: buildInput() });
    actor.start();
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });

    const oversized = JSON.stringify({ documentId: 'x', padding: 'a'.repeat(MAX_DOCUMENT_BYTES + 1) });
    actor.send({ type: 'OPEN_TEXT', text: oversized });
    await waitFor(actor, (s) => s.context.document.diagnostics.length > 0, { timeout: 5_000 });

    const message = actor.getSnapshot().context.document.diagnostics[0].message;
    assert.match(message, /over the \d+ byte cap/);

    const snapshot = actor.getPersistedSnapshot();
    actor.stop();

    const html = renderWithSnapshot(React.createElement(WorldStudioAlerts, { onReveal: () => {} }), buildInput(), snapshot);
    assert.ok(html.includes('byte cap'), 'the oversize refusal message is shown in the alerts');
  });
}
