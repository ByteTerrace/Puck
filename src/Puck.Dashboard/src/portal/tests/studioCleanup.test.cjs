// Regressions the studio's preview and views have held: exact bigint comparison, large projections, and — against
// the published engine as the world engine — preview history replay, handle release, and geometry reuse. The
// real-engine tests stand a fixed document in for the check's compileSource and composeSource, so they run on an
// engine build with or without the source exports.
const assert = require('node:assert/strict');
const { test, before } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { buildOfficialFixture, fakeEngine, bootSequence, memoryStorage } = require('./support/studioFixture.cjs');

const { createActor, waitFor } = require('xstate');
const { studioMachine } = require('../src/machines/studioMachine.ts');
const { bootEngineFromLocalBundle } = require('../src/native/engineBoot.ts');
const { computeGeometry } = require('../src/machines/studio/geometry.ts');
const { compilePreview } = require('../src/machines/studio/preview.ts');
const { sameCells, StateMatrixView } = require('../src/components/world/StateMatrixView.tsx');
const { projectScene } = require('../src/authoring/sceneProjection.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');
const { StudioContext } = require('../src/context/StudioContext.tsx');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MantineProvider } = require('@mantine/core');

test('state comparison preserves bigint precision, cell identity and the carried case', () => {
  const cells = [{ key: 'x', value: { kind: 'Int', value: 9223372036854775807n } }];
  assert.equal(sameCells(cells, [{ ...cells[0] }]), true);
  assert.equal(sameCells(cells, [{ ...cells[0], value: { kind: 'Int', value: 9223372036854775806n } }]), false);
  assert.equal(sameCells(cells, [{ ...cells[0], key: 'y' }]), false);
  assert.equal(sameCells(cells, [{ key: 'x', value: { kind: 'Text', value: 'one' } }]), false);
  // The same 64-bit word under another kind is a different value, which the sibling-nullable shape could not say.
  assert.equal(sameCells([{ key: 'f', value: { kind: 'Int', value: 1n } }], [{ key: 'f', value: { kind: 'Bool', value: true } }]), false);
  assert.equal(sameCells([{ key: 'f', value: null }], [{ key: 'f', value: null }]), true);
});

test('large irregular projections retain ordinal identity without argument-count overflow', () => {
  const cells = Array.from({ length: 140000 }, (_, ordinal) => ({ ordinal, key: String(ordinal), x: ordinal / 8, y: ordinal % 3, z: -ordinal / 4 }));
  const projected = projectScene(cells, 0.25, 2);
  assert.equal(projected.cells.length, cells.length);
  assert.equal(projected.cells.at(-1).ordinal, cells.length - 1);
  assert.equal(projected.cells[0].grid.col, 0);
  assert.equal(projected.cells[0].grid.row, cells.length - 1);
  assert.deepEqual(projected.layers, [0, 1, 2]);
  assert.ok(projected.bounds.max.every(Number.isFinite));
});

const bundle = path.resolve(__dirname, '../../../../Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle');
const hasBundle = fs.existsSync(path.join(bundle, 'main.mjs'));
const composed = JSON.stringify({ schema: 'puck.world.definition.v1', documentId: 'cleanup-fixture', state: { world: [{ name: 'counter', kind: 'Int', value: 0 }] } });
let engine;
before(async () => { if (hasBundle) engine = await bootEngineFromLocalBundle(bundle); });
const native = (name, run) => test(name, { skip: hasBundle ? false : 'Publish Puck.World.Browser to run the real-engine cleanup regressions.' }, run);

/** Opens the fixture's counter with the published engine as the world engine; its composition is `composed`. */
async function start() {
  const language = fakeEngine();
  const world = {
    ...engine,
    mountSources: async () => {},
    writeSource: async () => {},
    compileSource: async () => ({ ok: true, document: composed, worlds: [], diagnostics: [], sourceMap: {} }),
    composeSource: async () => ({ ok: true, composed, diagnostics: [] }),
  };
  const fixture = await buildOfficialFixture();
  const input = {
    official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl,
    bootEngine: bootSequence(language, world).bootEngine, draftStore: new LocalDraftStore(memoryStorage()),
  };
  const actor = createActor(studioMachine, { input }).start();
  await waitFor(actor, (s) => s.matches('ready'), { timeout: 5000 });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await waitFor(actor, (s) => s.matches({ ready: { workspace: 'open' } }), { timeout: 5000 });
  language.publish('counter.puck', 0, []);
  await waitFor(actor, (s) => s.context.workspace.island?.ok === true, { timeout: 10000 });
  return { actor, input };
}
async function previewEvent(actor, event) {
  actor.send(event);
  return (await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 10000 })).context.preview;
}

native('history replays the selected tick and a new write discards the abandoned future', async (t) => {
  const { actor, input } = await start();
  t.after(() => actor.stop());
  await previewEvent(actor, { type: 'PREVIEW_START' });
  await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'counter', value: 3n, write: 'set' });
  const first = await previewEvent(actor, { type: 'PREVIEW_TICK' });
  assert.equal(first.snapshots[1].scriptLength, 2);
  await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'counter', value: 7n, write: 'set' });
  await previewEvent(actor, { type: 'PREVIEW_TICK' });
  let current = await previewEvent(actor, { type: 'PREVIEW_UNDO' });
  assert.equal(current.tick, 1n);
  assert.equal(current.rows[0].cells[0].value.value, 3n);
  current = await previewEvent(actor, { type: 'PREVIEW_REDO' });
  assert.equal(current.tick, 2n);
  assert.equal(current.rows[0].cells[0].value.value, 7n);
  await previewEvent(actor, { type: 'PREVIEW_UNDO' });
  const refused = await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'missing-row', value: 9n, write: 'set' });
  assert.equal(refused.snapshots.length, 3);
  assert.equal(refused.script.length, 4);
  assert.ok(refused.refusals.length > 0);
  await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'counter', value: 9n, write: 'set' });
  current = await previewEvent(actor, { type: 'PREVIEW_TICK' });
  assert.equal(current.tick, 2n);
  assert.equal(current.script.length, 4);
  assert.equal(current.snapshots.length, 3);
  const html = renderToStaticMarkup(React.createElement(MantineProvider, null,
    React.createElement(StudioContext.Provider, { options: { input, snapshot: actor.getPersistedSnapshot() } }, React.createElement(StateMatrixView))));
  assert.match(html, /counter/);
  assert.match(html, /changed/);
  await previewEvent(actor, { type: 'PREVIEW_UNDO' });
  current = await previewEvent(actor, { type: 'PREVIEW_REDO' });
  assert.equal(current.rows[0].cells[0].value.value, 9n);
});

native('a jump to a recorded tick restores that snapshot\'s hash, and a jump to the current cursor is not taken', async (t) => {
  const { actor } = await start();
  t.after(() => actor.stop());
  const started = await previewEvent(actor, { type: 'PREVIEW_START' });
  const initialHash = started.snapshots[0].hash;
  assert.equal(await engine.stateHash(started.handle), initialHash);
  await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'counter', value: 3n, write: 'set' });
  await previewEvent(actor, { type: 'PREVIEW_TICK' });
  await previewEvent(actor, { type: 'PREVIEW_WRITE', row: 'counter', value: 7n, write: 'set' });
  const latest = await previewEvent(actor, { type: 'PREVIEW_TICK' });
  assert.equal(latest.cursor, 2);
  assert.notEqual(latest.snapshots[2].hash, initialHash);

  const here = actor.getSnapshot();
  assert.equal(here.can({ type: 'JUMP_TO_TICK', index: 2 }), false, 'the cursor is already there');
  assert.equal(here.can({ type: 'JUMP_TO_TICK', index: 3 }), false, 'no snapshot is recorded there');
  assert.equal(here.can({ type: 'JUMP_TO_TICK', index: 0.5 }), false);
  actor.send({ type: 'JUMP_TO_TICK', index: 2 });
  assert.equal(actor.getSnapshot().hasTag('preview-busy'), false, 'nothing replays');
  assert.equal(actor.getSnapshot().context.preview.handle, latest.handle);

  assert.equal(here.can({ type: 'JUMP_TO_TICK', index: 0 }), true);
  const first = await previewEvent(actor, { type: 'JUMP_TO_TICK', index: 0 });
  assert.equal(first.cursor, 0);
  assert.equal(first.tick, 0n);
  assert.equal(first.rows[0].cells[0].value.value, 0n);
  assert.equal(await engine.stateHash(first.handle), initialHash);
  assert.deepEqual(first.refusals, []);

  const back = await previewEvent(actor, { type: 'JUMP_TO_TICK', index: 2 });
  assert.equal(back.cursor, 2);
  assert.equal(back.rows[0].cells[0].value.value, 7n);
  assert.equal(await engine.stateHash(back.handle), latest.snapshots[2].hash);
});

native('cancelling compilation releases the handle when the engine eventually answers', async () => {
  let resume, compiledHandle;
  const pending = new Promise((resolve) => { resume = resolve; });
  const controller = new AbortController();
  const wrapped = { ...engine, compile: async (source) => { const result = await engine.compile(source); compiledHandle = result.handle; await pending; return result; } };
  const compiling = compilePreview(wrapped, composed, controller.signal);
  controller.abort();
  resume();
  await assert.rejects(compiling, /abort/i);
  await assert.rejects(engine.stateHash(compiledHandle));
});

native('stopping the actor releases its accepted preview handle', async () => {
  const { actor } = await start();
  const preview = await previewEvent(actor, { type: 'PREVIEW_START' });
  actor.stop();
  await new Promise((resolve) => setImmediate(resolve));
  await assert.rejects(engine.stateHash(preview.handle));
});

native('unchanged topology geometry reuses the real engine answer and cell array identity', async () => {
  let calls = 0;
  const wrapped = { ...engine, cells: async (json) => { calls++; return engine.cells(json); } };
  const document = { state: { lattices: [{ $type: 'grid', name: 'board', width: 3, depth: 2, cellSize: 1, band: 0.3, origin: [0, 0, 0] }] } };
  const first = await computeGeometry(wrapped, document);
  assert.deepEqual(first.diagnostics, []);
  const second = await computeGeometry(wrapped, { ...document, metadata: { custom: { note: 'unrelated' } } });
  assert.equal(calls, 1);
  assert.equal(second.geometry.board, first.geometry.board);
  const third = await computeGeometry(wrapped, { state: { lattices: [{ ...document.state.lattices[0], width: 4 }] } });
  assert.equal(calls, 2);
  assert.notEqual(third.geometry.board, first.geometry.board);
});
