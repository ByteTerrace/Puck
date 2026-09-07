// Exercises the real studioMachine end to end: a real xstate actor, a real Puck.World.Browser
// engine (inline mode over the published AppBundle), and the real official content tree read from
// local disk through a file-backed `fetch` stand-in — the same "drive the shipped bytes" posture
// as tests/engine-wasm.test.cjs and tests/official.test.cjs, just wired together.
//
// bootEngineFromOfficial (native/engineBoot.ts) does not exist in this worktree yet — it is owned
// by a work package built in parallel. Per this package's own contract, `testBootEngine` below
// stands in for it: it boots through native/engineHost's createEngineHost in 'inline' mode over
// the local AppBundle and reproduces bootEngineFromOfficial's own documented version check. The
// machine itself never imports engineBoot.ts — it only calls whatever `bootEngine` function its
// own `input` is given (see studio/types.ts's BootEngine remarks); this file is that seam's one
// test-time implementation.
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
const officialDir = path.join(root, 'artifacts', 'official');
const officialManifest = path.join(officialDir, 'dev', 'manifest.json');

const HAS_FIXTURES = fs.existsSync(mainMjs) && fs.existsSync(officialManifest);

if (!HAS_FIXTURES) {
  test(`studioMachine (SKIPPED: no local AppBundle/official fixtures under ${appBundleDir} / ${officialDir} — ` +
    `publish Puck.World.Browser and run 'puck.exe official build' to produce them)`, { skip: true }, () => {});
} else {
  /** Serves `artifacts/official` straight off disk through the same `fetch`-shaped interface
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
      VITE_PUCK_OFFICIAL_CHANNEL: 'dev',
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

  test('OPEN_OFFICIAL puck.world.json reads role world with clean diagnostics and the arcade deferrals', async () => {
    actor.send({ type: 'OPEN_OFFICIAL', name: 'puck.world.json' });
    const snapshot = await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    assert.equal(snapshot.context.document.name, 'puck.world.json');
    assert.equal(snapshot.context.document.role, 'world');
    assert.deepEqual(snapshot.context.document.diagnostics, []);
    assert.ok(snapshot.context.document.deferred.length >= 3, `expected at least 3 deferrals, got ${JSON.stringify(snapshot.context.document.deferred)}`);
    assert.ok(snapshot.context.document.deferred.some((line) => line.includes('screen-machine engine')));
  });

  test('OPEN_OFFICIAL games/tictactoe.world.json reads role fragment and composes clean', async () => {
    actor.send({ type: 'OPEN_OFFICIAL', name: 'games/tictactoe.world.json' });
    const snapshot = await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });

    assert.equal(snapshot.context.document.name, 'games/tictactoe.world.json');
    assert.equal(snapshot.context.document.role, 'fragment');
    assert.deepEqual(snapshot.context.document.diagnostics, []);
    assert.ok(snapshot.context.document.composed, 'a fragment validates through composeTree and records composed text');
  });

  test('EDIT_DOCUMENT duplicating a rule name diagnoses it; UNDO clears it', async () => {
    // A dedicated actor over a tiny synthetic standalone world (role 'world', no basis/imports —
    // engine.parse alone, not composeTree), NOT any real official fragment: every real
    // multi-fragment board game either carries an Int64.Min/MaxValue sentinel a JSON.parse ->
    // setAt -> JSON.stringify round trip cannot preserve exactly (tictactoe.world.json — see
    // tests/engine-wasm.test.cjs's own remarks and machines/studio/document.ts's EDIT_DOCUMENT
    // remarks) or references shared `kits`/`looks`/aggregator state rows that only resolve inside
    // the real island (chess, klondike, and siblings) — neither of which this test exists to probe.
    // A minimal, self-contained document sidesteps both and stays fast (no composeTree at all).
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

  test('PAINT_CELLS writes cells[] entries on the named state.world row as one revision', async () => {
    // A dedicated actor + a tiny synthetic standalone world, for the same reason the duplicate-
    // rule-name test above uses one: PAINT_CELLS reserializes the WHOLE document exactly like
    // EDIT_DOCUMENT does, and tictactoe.world.json's own Int64.Min/MaxValue sentinel does not
    // survive that round trip (see this file's earlier remarks). A minimal grid topology + one
    // bound row is all this test needs to prove the paint mechanism itself.
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
    assert.equal(byKey.get('0'), 7);
    assert.equal(byKey.get('1'), 7);
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

  test('preview: start, write a cell, tick — the snapshot hash changes and matches engine.stateHash', async () => {
    actor.send({ type: 'PREVIEW_START' });
    const ready = await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 120_000 });
    assert.equal(ready.context.preview.status, 'ready');
    assert.equal(ready.context.preview.snapshots.length, 1);
    const initialHash = ready.context.preview.snapshots[0].hash;

    const row = ready.context.preview.rows.find((r) => r.kind === 'Int' && !r.keyed);
    assert.ok(row, 'tictactoe carries at least one scalar Int row');
    // Write a value guaranteed to differ from whatever the row already holds, so the hash change
    // this test checks for cannot be a false negative from writing the row's own current value.
    const nextValue = (row.cells[0]?.value ?? 0n) + 1n;

    actor.send({ type: 'PREVIEW_WRITE', row: row.name, value: nextValue, write: 'set' });
    await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }) && s.context.preview.script.length === 1, { timeout: 120_000 });

    actor.send({ type: 'PREVIEW_TICK' });
    const ticked = await waitFor(
      actor,
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
    const before = actor.getSnapshot();
    const initialHash = before.context.preview.snapshots[0].hash;

    actor.send({ type: 'JUMP_TO_TICK', index: 0 });
    const restored = await waitFor(
      actor,
      (s) => s.matches({ ready: { preview: 'ready' } }) && s.context.preview.cursor === 0,
      { timeout: 120_000 },
    );

    assert.equal(restored.context.preview.tick, 0n);
    const liveHash = await restored.context.engine.stateHash(restored.context.preview.handle);
    assert.equal(liveHash, initialHash);
  });

  test('a document edit during an active preview stops it', async () => {
    const before = actor.getSnapshot();
    assert.equal(before.context.preview.status, 'ready');

    actor.send({
      type: 'EDIT_DOCUMENT',
      path: ['metadata', 'custom', 'note'],
      value: 'stop the preview',
      label: 'touch metadata',
    });

    const stopped = await waitFor(actor, (s) => s.matches({ ready: { preview: 'idle' } }), { timeout: 120_000 });
    assert.equal(stopped.context.preview.handle, null);
    assert.equal(stopped.context.preview.snapshots.length, 0);

    // The document region keeps working independently — the whole point of "editing continues".
    await waitFor(actor, (s) => s.matches({ ready: { document: 'idle' } }), { timeout: 120_000 });
  });

  test('drafts round trip through SAVE_DRAFT/LOAD_DRAFT and cap at ten revisions', async () => {
    const draftActor = startActor();
    await waitFor(draftActor, (s) => s.matches('ready'), { timeout: 120_000 });
    // A small synthetic standalone world, not a real official fragment: a real fragment like
    // tictactoe.world.json carries Int64.Min/MaxValue sentinels (see tests/engine-wasm.test.cjs's
    // own remarks) that a JSON.parse->JSON.stringify round trip cannot preserve exactly — a real,
    // pre-existing limitation of the plain-JS-object edit path this test does not exist to probe.
    // engine.parse (the standalone path) is also far cheaper than a full-island composeTree, which
    // keeps this drafts-focused test fast.
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
      await waitFor(draftActor, (s) => s.matches({ ready: { document: 'idle' } }) && s.context.document.cleanRevision === s.context.document.revision, { timeout: 120_000 });
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
