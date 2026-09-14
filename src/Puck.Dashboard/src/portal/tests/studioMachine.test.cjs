// Exercises the real studioMachine end to end: a real xstate actor, a real Puck.World.Browser
// engine (inline mode over the published AppBundle), and the real official content tree read from
// local disk through a file-backed `fetch` stand-in — the same "drive the shipped bytes" posture
// as tests/engine-wasm.test.cjs and tests/official.test.cjs, just wired together.
//
// testBootEngine boots the local AppBundle and checks its schema/commit against the official
// tree. The machine receives it through its bootEngine input; browser-specific boot behavior
// belongs to native/engineBoot.ts.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');
const { pathToFileURL, fileURLToPath } = require('node:url');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { createActor, waitFor } = require('xstate');
const { studioMachine } = require('../src/machines/studioMachine.ts');
const { resolveOfficial } = require('../src/official/officialBase.ts');
// native/engineHost.ts's 'worker' branch carries a literal `import.meta.url` token, which makes
// Node's CommonJS-require-of-.ts loader misdetect the whole file as an ES module (see
// official/officialBase.ts's own header remarks on this exact gotcha) — `wrapRawExports` alone
// carries no such token, so it is required directly. `createInlineWorldEngine` itself is NOT used
// here: TypeScript's CommonJS output downlevels its `await import(options.engineEntryUrl)` into an
// interop `require(...)` call (real dynamic `import()` survives untouched only in true ESM output),
// and Node's CJS `require` cannot resolve a `file://` URL string the way a real dynamic `import()`
// can — the exact mismatch `tests/engine-wasm.test.cjs` avoids by calling native `import()` directly
// from its own .cjs file rather than through a transpiled .ts module. This test does the same: a
// real `import()` loads `main.mjs`, and `wrapRawExports` (the same marshalling `inlineHost.ts`'s own
// `createInlineWorldEngine` calls) wraps it into a `WorldEngine`.
const { wrapRawExports } = require('../src/native/inlineHost.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');
const { validateDocument } = require('../src/machines/studio/document.ts');

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
  test(`studioMachine (SKIPPED: no local AppBundle/official fixtures under ${appBundleDir} / ${officialDir} — ` +
    `publish Puck.World.Browser and run 'puck.exe official build' to produce them)`, { skip: true }, () => {});
} else {
  /** Serves the selected official tree off disk through the same `fetch`-shaped interface
   * officialClient.ts calls — a local twin of tests/official.test.cjs's own in-memory fakeFetch. */
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

  /** The test-time stand-in for bootEngineFromOfficial: boots inline over the local AppBundle and
   * reproduces that contract's own schema/commit check (see this file's header remarks). */
  async function testBootEngine(officialLoad) {
    const module = await import(pathToFileURL(mainMjs).href);
    const raw = await module.createEngine();
    const engine = wrapRawExports(raw);
    const version = await engine.version();
    if (version.schemaVersion !== officialLoad.build.worldSchema || version.commit !== officialLoad.build.commit) {
      await engine.dispose();
      throw new Error(
        `engine build mismatch: schemaVersion ${version.schemaVersion} vs official ${officialLoad.build.worldSchema}, ` +
        `commit ${version.commit} vs official ${officialLoad.build.commit}`,
      );
    }
    return engine;
  }

  function memoryStorage() {
    const entries = new Map();
    return { getItem: (key) => entries.get(key) ?? null, setItem: (key, value) => entries.set(key, value) };
  }

  function startActor(overrides = {}) {
    const actor = createActor(studioMachine, {
      input: {
        official: resolvedOfficial(),
        engineMode: 'inline',
        fetchImpl: diskFetch,
        bootEngine: testBootEngine,
        draftStore: new LocalDraftStore(memoryStorage()),
        ...overrides,
      },
    });
    actor.start();
    return actor;
  }

  // A single shared actor carries the main narrative (boot -> open -> edit/undo -> geometry ->
  // preview -> stale-on-edit) in declaration order — node:test runs a file's top-level tests
  // sequentially by default, the same assumption tests/engine-wasm.test.cjs's shared `engineReady`
  // promise already relies on.
  let actor;

  test('boot reaches ready with build info from the real official manifest', async () => {
    actor = startActor();
    const snapshot = await waitFor(actor, (s) => s.matches('ready'), { timeout: 120_000 });

    assert.equal(snapshot.context.boot.status, 'ready');
    assert.equal(snapshot.context.boot.refusal, null);
    assert.ok(snapshot.context.boot.build);
    assert.equal(snapshot.context.boot.build.schemaVersion, 'puck.world.def.v1');
    assert.equal(snapshot.context.official.build.commit, snapshot.context.boot.build.commit);
    assert.equal(snapshot.context.engine !== null, true);
  });

  test('PREVIEW_START is refused while the document has never been validated (validation stays \'pending\')', async () => {
    // The freshly-booted actor's own document is still `createEmptyDocument()` — no OPEN_* event
    // has fired yet, so it has never gone through the document region's `validating` state.
    // `diagnostics.length === 0` is ALSO true here (a fresh document starts with none), which is
    // exactly the stale guard `document.validation` replaces — PREVIEW_START must still be refused.
    const snapshot = actor.getSnapshot();
    assert.equal(snapshot.context.document.validation, 'pending');
    assert.deepEqual(snapshot.context.document.diagnostics, []);

    actor.send({ type: 'PREVIEW_START' });
    const refused = await waitFor(actor, (s) => s.matches({ ready: { preview: 'refused' } }), { timeout: 120_000 });
    assert.ok(refused.context.preview.refusal && refused.context.preview.refusal.length > 0);
    assert.equal(refused.context.preview.handle, null);
    // The next test's OPEN_OFFICIAL is itself a document-mutating event, which the preview
    // region's own machine-wide handler bounces back to `.idle` regardless of this 'refused' start.
  });

  test('OPEN_OFFICIAL puck.world.json reads role world with clean diagnostics and the arcade deferrals', async () => {
    actor.send({ type: 'OPEN_OFFICIAL', name: 'puck.world.json' });
    const snapshot = await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    assert.equal(snapshot.context.document.name, 'puck.world.json');
    assert.equal(snapshot.context.document.role, 'world');
    assert.deepEqual(snapshot.context.document.diagnostics, []);
    assert.ok(snapshot.context.document.deferred.length >= 3, `expected at least 3 deferrals, got ${JSON.stringify(snapshot.context.document.deferred)}`);
    assert.ok(snapshot.context.document.deferred.some((line) => line.includes('screen-machine engine')));
  });

  // games/tictactoe.world.json is a BARE fragment (no basis/imports of its own) — per the island
  // fix (studio/document.ts's own validateDocument remarks), this now composes over the REAL
  // island (puck.world.json + every district/shard), not a minimal synthetic root, so `composed`
  // carries the whole island's own JSON with this fragment substituted, not just the fragment
  // alone. It also carries the Int64.MinValue/MaxValue sentinel `games/tictactoe.world.json`
  // authors as one row's `min`/`max` (see document/jsonText.ts's own remarks) — every assertion in
  // this and the next several tests reuses THIS SAME open (each full-island composeTree call is
  // measured in low-teens seconds on a dev machine; see this package's own COMMANDS report), rather
  // than re-opening it, to keep the suite's total wall time down.
  test('OPEN_OFFICIAL games/tictactoe.world.json reads role fragment and composes clean under the real island', async () => {
    actor.send({ type: 'OPEN_OFFICIAL', name: 'games/tictactoe.world.json' });
    const snapshot = await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    assert.equal(snapshot.context.document.name, 'games/tictactoe.world.json');
    assert.equal(snapshot.context.document.role, 'fragment');
    assert.deepEqual(snapshot.context.document.diagnostics, []);
    assert.equal(snapshot.context.document.validation, 'clean');
    assert.ok(snapshot.context.document.composed, 'a fragment validates through composeTree and records composed text');
    // Composing over the real island produces a document far larger than the bare fragment alone
    // (every district/shard is in there too) — the observable fingerprint of the island fix.
    assert.ok(snapshot.context.document.composed.length > snapshot.context.document.text.length * 5,
      `composed island text (${snapshot.context.document.composed.length} chars) should dwarf the bare fragment's own text (${snapshot.context.document.text.length} chars)`);
    assert.ok(snapshot.context.document.text.includes('9223372036854775807'),
      "tictactoe.world.json's own Int64.MaxValue sentinel must survive OPEN_OFFICIAL's own parse/reserialize exactly");
  });

  test('Int64 fidelity: EDIT_DOCUMENT on the real tictactoe fragment preserves its own Int64 sentinel exactly', async () => {
    const before = actor.getSnapshot();
    assert.ok(before.context.document.text.includes('9223372036854775807'));

    actor.send({
      type: 'EDIT_DOCUMENT',
      path: ['metadata', 'custom', 'note'],
      value: 'touched by the Int64 fidelity test',
      label: 'touch an unrelated field',
    });
    const edited = await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    assert.deepEqual(edited.context.document.diagnostics, []);
    assert.equal(edited.context.document.validation, 'clean');
    assert.equal(edited.context.document.value.metadata.custom.note, 'touched by the Int64 fidelity test');
    // The whole-document reserialize this edit triggered (setAt -> serializeDocumentText) must
    // leave the UNTOUCHED sentinel elsewhere in the very same document exactly as authored — a
    // plain JSON.parse -> JSON.stringify round trip would instead have rounded it.
    assert.ok(edited.context.document.text.includes('9223372036854775807'),
      'the Int64.MaxValue sentinel must survive an edit to an unrelated field exactly');
  });

  test('Int64 fidelity: a PAINT_CELLS on the real tictactoe fragment is clean and preserves the sentinel too', async () => {
    const before = actor.getSnapshot();
    const boardRow = before.context.document.value.state.world.find((row) =>
      row.domain && row.domain.$type === 'cellsOf');
    assert.ok(boardRow, 'tictactoe.world.json declares at least one cellsOf-domain board row');
    const revisionBefore = before.context.document.revision;

    actor.send({ type: 'PAINT_CELLS', topology: 'board', row: boardRow.name, ordinals: [0], value: 1n });
    const painted = await waitFor(
      actor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.revision > revisionBefore,
      { timeout: 120_000 },
    );

    assert.deepEqual(painted.context.document.diagnostics, []);
    assert.equal(painted.context.document.validation, 'clean');
    const paintedRow = painted.context.document.value.state.world.find((row) => row.name === boardRow.name);
    const paintedCell = paintedRow.cells.find((cell) => cell.key === '0');
    assert.equal(typeof paintedCell.value, 'bigint', 'a painted cell value is a bigint, not a downcast number');
    assert.equal(paintedCell.value, 1n);
    assert.ok(painted.context.document.text.includes('9223372036854775807'),
      'the Int64.MaxValue sentinel elsewhere in the document must survive a paint on an unrelated row');
  });

  test('EDIT_DOCUMENT duplicating a rule name diagnoses it; UNDO clears it', async () => {
    // A dedicated actor over a tiny synthetic standalone world (role 'world', no basis/imports —
    // engine.parse alone, not composeTree), NOT any real official fragment: a real fragment either
    // references shared `kits`/`looks`/aggregator state rows that only resolve inside the real
    // island (chess, klondike, billiards, and siblings — see the island-fix tests above) or costs a
    // full-island composeTree even when it doesn't (tictactoe). A minimal, self-contained document
    // sidesteps both and stays fast (no composeTree at all) — this test is about the diagnostic
    // wiring itself, not island composition.
    const editActor = startActor();
    await waitFor(editActor, (s) => s.matches('ready'), { timeout: 120_000 });
    editActor.send({
      type: 'OPEN_TEXT',
      name: 'dup-rule-fixture.world.json',
      text: JSON.stringify({
        schema: 'puck.world.def.v1',
        documentId: 'dup-rule-fixture',
        state: { world: [{ name: 'counter', kind: 'Int', value: 0 }] },
        rules: [{ name: 'r1', effects: [{ $type: 'setState', state: 'counter', value: 1 }] }],
      }),
    });
    const opened = await waitFor(editActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.deepEqual(opened.context.document.diagnostics, []);
    assert.equal(opened.context.document.role, 'world');
    const rules = opened.context.document.value.rules;
    assert.equal(rules.length, 1);

    editActor.send({
      type: 'EDIT_DOCUMENT',
      path: ['rules', rules.length],
      value: JSON.parse(JSON.stringify(rules[0])),
      label: 'duplicate first rule',
    });
    const refused = await waitFor(
      editActor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.diagnostics.length > 0,
      { timeout: 120_000 },
    );
    assert.ok(refused.context.document.diagnostics.some((d) => d.message.includes('r1')),
      JSON.stringify(refused.context.document.diagnostics));
    assert.equal(refused.context.document.value.rules.length, 2, 'the refused edit still applies to the document — the user sees diagnostics against it');

    editActor.send({ type: 'UNDO' });
    const restored = await waitFor(
      editActor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.diagnostics.length === 0,
      { timeout: 120_000 },
    );
    assert.deepEqual(restored.context.document.diagnostics, []);
    assert.equal(restored.context.document.value.rules.length, 1);

    editActor.stop();
  });

  test('supersedable validation: rapid edits retain both revisions and validate the latest value', async () => {
    // Edits commit synchronously and remain undoable; expensive validation is debounced.
    const rapidActor = startActor();
    await waitFor(rapidActor, (s) => s.matches('ready'), { timeout: 120_000 });
    rapidActor.send({
      type: 'OPEN_TEXT',
      name: 'rapid-fixture.world.json',
      text: JSON.stringify({ schema: 'puck.world.def.v1', documentId: 'rapid-fixture', metadata: { custom: { marker: 'initial' } } }),
    });
    const opened = await waitFor(rapidActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.equal(opened.context.document.value.metadata.custom.marker, 'initial');
    const revisionBefore = opened.context.document.revision;

    rapidActor.send({ type: 'EDIT_DOCUMENT', path: ['metadata', 'custom', 'marker'], value: 'first', label: 'set marker to first' });
    rapidActor.send({ type: 'EDIT_DOCUMENT', path: ['metadata', 'custom', 'marker'], value: 'second', label: 'set marker to second' });

    const settled = await waitFor(
      rapidActor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.revision > revisionBefore,
      { timeout: 120_000 },
    );

    assert.equal(settled.context.document.value.metadata.custom.marker, 'second', 'only the newer edit\'s value lands');
    assert.equal(settled.context.document.label, 'set marker to second');
    assert.equal(settled.context.document.revision, revisionBefore + 2, 'both edits remain in document history');
    assert.deepEqual(settled.context.document.diagnostics, []);
    assert.equal(settled.context.document.validation, 'clean');

    rapidActor.stop();
  });

  test('PAINT_CELLS writes cells[] entries on the named state.world row as one revision', async () => {
    // A dedicated actor + a tiny synthetic standalone world (engine.parse alone, no composeTree):
    // this test proves the bulk-paint MECHANISM itself (one revision, undo restores the prior
    // cells) fast and in isolation. Int64 fidelity specifically — a paint leaving an UNTOUCHED
    // sentinel elsewhere in a real document exactly as authored — is the real
    // games/tictactoe.world.json fragment's own job above, where composing under the real island
    // is unavoidable anyway.
    const paintActor = startActor();
    await waitFor(paintActor, (s) => s.matches('ready'), { timeout: 120_000 });
    paintActor.send({
      type: 'OPEN_TEXT',
      name: 'paint-fixture.world.json',
      text: JSON.stringify({
        schema: 'puck.world.def.v1',
        documentId: 'paint-fixture',
        state: {
          lattices: [{ $type: 'grid', name: 'board', width: 2, depth: 2, cellSize: 1, band: 0.3, origin: [0, 0, 0] }],
          world: [{ name: 'cell', kind: 'Int', domain: { $type: 'cellsOf', topology: 'board' } }],
        },
      }),
    });
    const before = await waitFor(paintActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.deepEqual(before.context.document.diagnostics, []);
    const revisionBefore = before.context.document.revision;
    const boardRow = before.context.document.value.state.world[0];

    paintActor.send({ type: 'PAINT_CELLS', topology: 'board', row: boardRow.name, ordinals: [0, 1], value: 7n });
    const painted = await waitFor(
      paintActor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.revision > revisionBefore,
      { timeout: 120_000 },
    );

    assert.deepEqual(painted.context.document.diagnostics, []);
    const paintedRow = painted.context.document.value.state.world.find((row) => row.name === boardRow.name);
    const byKey = new Map(paintedRow.cells.map((cell) => [cell.key, cell.value]));
    // A painted cell value is ALWAYS a bigint (see paintCells's own remarks) — never downcast to a
    // plain number even when it would fit safely, per this project's own "every 64-bit value is a
    // bigint" rule.
    assert.equal(typeof byKey.get('0'), 'bigint');
    assert.equal(byKey.get('0'), 7n);
    assert.equal(byKey.get('1'), 7n);
    // A bulk paint is ONE revision, not one per ordinal.
    const revisionAfterPaint = painted.context.document.revision;
    assert.equal(revisionAfterPaint, revisionBefore + 1);

    // `revision` is a monotonic counter (see studio/types.ts's own remarks) — UNDO bumps it again
    // while restoring the VALUE, it never counts back down to `revisionBefore`.
    paintActor.send({ type: 'UNDO' });
    const undone = await waitFor(paintActor, (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.revision > revisionAfterPaint, { timeout: 120_000 });
    const restoredRow = undone.context.document.value.state.world.find((row) => row.name === boardRow.name);
    assert.deepEqual(restoredRow.cells ?? [], boardRow.cells ?? []);

    paintActor.stop();
  });

  test('geometry carries the tictactoe board topology\'s cells with ordinals matching the engine\'s own answer', async () => {
    const snapshot = actor.getSnapshot();
    const lattices = snapshot.context.document.value.state.lattices;
    assert.ok(Array.isArray(lattices) && lattices.length > 0, 'tictactoe.world.json declares at least one lattice topology');
    const topologyName = lattices[0].name;

    const expected = await snapshot.context.engine.cells(JSON.stringify(lattices[0]));
    assert.equal(expected.ok, true, JSON.stringify(expected));

    const cells = snapshot.context.geometry[topologyName];
    assert.ok(Array.isArray(cells), `no geometry recorded for topology '${topologyName}'`);
    assert.deepEqual(
      cells.map((c) => c.ordinal).sort((a, b) => a - b),
      expected.cells.map((c) => c.ordinal).sort((a, b) => a - b),
    );
  });

  // The island fix's own correctness proof: games/billiards.world.json refuses to validate under
  // the OLD bare-basis synthetic root (it names the island's own body/look rows and host
  // registers — see studio/document.ts's own validateDocument remarks) but must validate clean
  // once composed over the real island, exactly as opening the real game would. Calls
  // `validateDocument` directly against the SAME already-booted engine/official the main
  // narrative's own `actor` carries — reusing the one engine boot rather than starting another,
  // and never touching `actor`'s own document state (the geometry test above already read it).
  test('the island fix: games/billiards.world.json refuses under a bare basis but validates clean under the real island', async () => {
    const { engine, official } = actor.getSnapshot().context;
    const billiardsText = await official.documents.get('games/billiards.world.json');
    const billiardsValue = JSON.parse(billiardsText);

    // Prove the OLD design's premise: this fragment genuinely refuses under a minimal synthetic
    // root over standard.basis.json — the exact shape validateDocument used to build for every
    // bare fragment before the island fix.
    const bareRootName = '$bare-basis-probe-root.json';
    const bareRootJson = JSON.stringify({
      schema: 'puck.world.def.v1',
      basis: 'standard.basis.json',
      imports: [{ document: 'games/billiards.world.json', as: null }],
    });
    const documents = {};
    for (const name of official.manifest.documents.map((d) => d.name)) {
      documents[name] = await official.documents.get(name);
    }
    const underBareBasis = await engine.composeTree(
      bareRootName,
      { ...documents, [bareRootName]: bareRootJson },
      { name: 'games/billiards.world.json', json: billiardsText },
    );
    assert.equal(underBareBasis.ok, false, 'billiards.world.json is expected to refuse under a bare basis by design');

    const outcome = await validateDocument(engine, official, {
      name: 'games/billiards.world.json',
      role: 'fragment',
      text: billiardsText,
      value: billiardsValue,
    });
    assert.equal(outcome.ok, true, JSON.stringify(outcome.diagnostics));
    assert.deepEqual(outcome.diagnostics, []);
  });

  // A duplicate NAME in a collection the real island MERGES across every district (`rules`,
  // `state.world`, `kits`, …) is diagnosed against the ISLAND ROOT, not the fragment — verified
  // directly (not asserted blind): duplicating tictactoe's own first rule name produces
  // `"puck.world.json: $.rules cannot merge by 'name': …"`, correctly prefixed per composeTree's
  // own "belongs to a different document" contract, because the merged `rules` LIST is genuinely
  // the root's own concern once every district's rules are folded together. `exports` is not
  // merged — it is validated against the island's OWN import graph, and a duplicate entry there is
  // reported by NAMING the edited fragment directly, proving composeTree threads the edited
  // document's own identity through to the diagnostic instead of losing it against the island root.
  test('EDIT_DOCUMENT duplicating an exports entry on the real tictactoe fragment is diagnosed by the fragment\'s own name', async () => {
    const before = actor.getSnapshot();
    const actions = before.context.document.value.exports.actions;
    assert.ok(Array.isArray(actions) && actions.length > 0);
    const duplicatedAction = actions[0];

    actor.send({
      type: 'EDIT_DOCUMENT',
      path: ['exports', 'actions', actions.length],
      value: duplicatedAction,
      label: 'duplicate an exports.actions entry',
    });
    const refused = await waitFor(
      actor,
      (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.diagnostics.length > 0,
      { timeout: 120_000 },
    );

    assert.equal(refused.context.document.validation, 'refused');
    assert.ok(
      refused.context.document.diagnostics.some((d) =>
        d.message.includes('games/tictactoe.world.json') && d.message.includes(duplicatedAction)),
      JSON.stringify(refused.context.document.diagnostics),
    );
  });

  // Preview mechanics (compile/write/tick/undo/reset, and a document edit stopping a live preview)
  // are state-machine plumbing, not island-composition content — a real fragment adds nothing here
  // but a ~13s composeTree PLUS a full-island `compile` on every PREVIEW_START/JUMP_TO_TICK/
  // RESET_WORLD (compiling the composed island, not just the fragment; see studio/document.ts's own
  // validateDocument remarks on the island fix's real cost). A small standalone world (engine.parse,
  // no composeTree; compile of a few bytes, not the whole MMO island) proves the same mechanics in
  // milliseconds instead, on its own dedicated actor so it never depends on the main narrative's own
  // (now real-content, now-diagnosed-by-the-duplicate-rule-test) `actor`.
  let previewActor;

  test('preview: start, write a cell, tick — the snapshot hash changes and matches engine.stateHash', async () => {
    previewActor = startActor();
    await waitFor(previewActor, (s) => s.matches('ready'), { timeout: 120_000 });
    previewActor.send({
      type: 'OPEN_TEXT',
      name: 'preview-fixture.world.json',
      text: JSON.stringify({
        schema: 'puck.world.def.v1',
        documentId: 'preview-fixture',
        state: { world: [{ name: 'counter', kind: 'Int', value: 0 }] },
      }),
    });
    await waitFor(previewActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    previewActor.send({ type: 'PREVIEW_START' });
    const ready = await waitFor(previewActor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 120_000 });
    assert.equal(ready.context.preview.status, 'ready');
    assert.equal(ready.context.preview.snapshots.length, 1);
    const initialHash = ready.context.preview.snapshots[0].hash;

    const row = ready.context.preview.rows.find((r) => r.kind === 'Int' && !r.keyed);
    assert.ok(row, 'the fixture carries one scalar Int row');
    // Write a value guaranteed to differ from whatever the row already holds, so the hash change
    // this test checks for cannot be a false negative from writing the row's own current value.
    const nextValue = (row.cells[0]?.value ?? 0n) + 1n;

    previewActor.send({ type: 'PREVIEW_WRITE', row: row.name, value: nextValue, write: 'set' });
    await waitFor(previewActor, (s) => s.matches({ ready: { preview: 'ready' } }) && s.context.preview.script.length === 1, { timeout: 120_000 });

    previewActor.send({ type: 'PREVIEW_TICK' });
    const ticked = await waitFor(
      previewActor,
      (s) => s.matches({ ready: { preview: 'ready' } }) && s.context.preview.snapshots.length === 2,
      { timeout: 120_000 },
    );

    assert.equal(ticked.context.preview.tick, 1n);
    const lastSnapshot = ticked.context.preview.snapshots[1];
    assert.notEqual(lastSnapshot.hash, initialHash);
    const liveHash = await ticked.context.engine.stateHash(ticked.context.preview.handle);
    assert.equal(liveHash, lastSnapshot.hash);
  });

  test('JUMP_TO_TICK 0 restores the initial hash', async () => {
    const before = previewActor.getSnapshot();
    const initialHash = before.context.preview.snapshots[0].hash;

    previewActor.send({ type: 'JUMP_TO_TICK', index: 0 });
    const restored = await waitFor(
      previewActor,
      (s) => s.matches({ ready: { preview: 'ready' } }) && s.context.preview.cursor === 0,
      { timeout: 120_000 },
    );

    assert.equal(restored.context.preview.tick, 0n);
    const liveHash = await restored.context.engine.stateHash(restored.context.preview.handle);
    assert.equal(liveHash, initialHash);
  });

  test('a document edit during an active preview stops it', async () => {
    const before = previewActor.getSnapshot();
    assert.equal(before.context.preview.status, 'ready');

    previewActor.send({
      type: 'EDIT_DOCUMENT',
      path: ['metadata', 'custom', 'note'],
      value: 'stop the preview',
      label: 'touch metadata',
    });

    const stopped = await waitFor(previewActor, (s) => s.matches({ ready: { preview: 'idle' } }), { timeout: 120_000 });
    assert.equal(stopped.context.preview.handle, null);
    assert.equal(stopped.context.preview.snapshots.length, 0);

    // The document region keeps working independently — the whole point of "editing continues".
    await waitFor(previewActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    previewActor.stop();
  });

  test('drafts round trip through SAVE_DRAFT/LOAD_DRAFT and cap at ten revisions', async () => {
    const draftActor = startActor();
    await waitFor(draftActor, (s) => s.matches('ready'), { timeout: 120_000 });
    // A small synthetic standalone world, not a real official fragment: engine.parse (the
    // standalone path) is far cheaper than a full-island composeTree, which keeps this
    // drafts-focused test fast — Int64 fidelity through this exact edit path is covered on real
    // content by the tictactoe fragment tests above.
    draftActor.send({
      type: 'OPEN_TEXT',
      text: JSON.stringify({ schema: 'puck.world.def.v1', documentId: 'draft-fixture' }),
      name: 'draft-fixture.world.json',
    });
    await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    for (let i = 0; i < 11; i += 1) {
      draftActor.send({
        type: 'EDIT_DOCUMENT',
        path: ['metadata', 'custom', 'counter'],
        value: String(i),
        label: `edit ${i}`,
      });
      await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
      draftActor.send({ type: 'SAVE_DRAFT', id: 'ttt-draft', title: 'TTT Draft' });
      await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.savedText === s.context.document.text, { timeout: 120_000 });
    }

    const store = draftActor.getSnapshot().context.machineInput.draftStore;
    const saved = store.load('ttt-draft');
    assert.ok(saved, 'the draft round-trips through the store the machine was given');
    assert.equal(saved.revisions.length, 10, 'capped at ten revisions');
    assert.ok(saved.revisions[0].text.includes('"counter": "10"'));

    draftActor.send({ type: 'LOAD_DRAFT', id: 'ttt-draft' });
    const loaded = await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.deepEqual(loaded.context.document.diagnostics, []);
    assert.equal(loaded.context.document.value.metadata.custom.counter, '10');

    draftActor.send({ type: 'DELETE_DRAFT', id: 'ttt-draft' });
    await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.equal(store.load('ttt-draft'), undefined);

    draftActor.stop();
  });

  test('a refused boot (tampered manifest) leaves the document region usable and preview refused by name', async () => {
    const tamperedFetch = (input) => {
      const url = typeof input === 'string' ? input : input.href;
      if (url.endsWith(path.sep === '\\' ? 'manifest.json' : '/manifest.json') || url.endsWith('manifest.json')) {
        return diskFetch(input).then(async (response) => {
          if (!response.ok) return response;
          const text = await response.text();
          const tampered = text.replace('"puck.world.def.v1"', '"not.the.real.schema"');
          return { ok: true, status: 200, text: async () => tampered, arrayBuffer: async () => new TextEncoder().encode(tampered).buffer };
        });
      }
      return diskFetch(input);
    };

    const refusedActor = startActor({ fetchImpl: tamperedFetch });
    const snapshot = await waitFor(refusedActor, (s) => s.matches('ready'), { timeout: 120_000 });

    assert.equal(snapshot.context.boot.status, 'refused');
    assert.ok(snapshot.context.boot.refusal && snapshot.context.boot.refusal.length > 0);
    assert.equal(snapshot.context.engine, null);

    // Editing continues in refused: OPEN_TEXT needs no engine at all.
    refusedActor.send({ type: 'OPEN_TEXT', text: JSON.stringify({ schema: 'puck.world.def.v1', documentId: 'standalone' }), name: 'standalone.json' });
    const opened = await waitFor(refusedActor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
    assert.equal(opened.context.document.name, 'standalone.json');
    assert.equal(opened.context.document.role, 'world');
    // Every engine-backed action answers with a diagnostic naming the refusal, rather than hanging.
    assert.ok(opened.context.document.diagnostics.some((d) => d.message.includes('engine unavailable')));

    refusedActor.send({ type: 'PREVIEW_START' });
    const previewRefused = await waitFor(refusedActor, (s) => s.matches({ ready: { preview: 'refused' } }), { timeout: 120_000 });
    assert.ok(previewRefused.context.preview.refusal && previewRefused.context.preview.refusal.length > 0);

    refusedActor.stop();
  });
}
