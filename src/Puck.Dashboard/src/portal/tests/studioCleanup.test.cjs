const assert = require('node:assert/strict');
const { test, before } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');
const options = { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX };
for (const extension of ['.ts', '.tsx']) require.extensions[extension] = (module, file) =>
  module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: options }).outputText, file);

const { createActor, fromPromise, waitFor } = require('xstate');
const { studioMachine } = require('../src/machines/studioMachine.ts');
const { openTextDocument, openDraftDocument, applyText, editDocument, undoDocument, redoDocument } = require('../src/machines/studio/document.ts');
const { selectIsDirty } = require('../src/machines/studio/selectors.ts');
const { bootEngineFromLocalBundle } = require('../src/native/engineBoot.ts');
const { computeGeometry } = require('../src/machines/studio/geometry.ts');
const { compilePreview } = require('../src/machines/studio/preview.ts');
const { sameCells, StateMatrixView } = require('../src/components/world/StateMatrixView.tsx');
const { projectScene } = require('../src/authoring/sceneProjection.ts');
const { LocalDraftStore, readDraftListing } = require('../src/document/localDrafts.ts');
const { StudioContext } = require('../src/context/StudioContext.tsx');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MantineProvider } = require('@mantine/core');

test('JSON typing, apply, undo, redo and saved-text detection agree on one applied revision', () => {
  const text = '{"metadata":{"custom":{"marker":"before","large":9223372036854775807}}}';
  const opened = openTextDocument(text, 'draft');
  assert.equal(selectIsDirty({ context: { document: opened } }), false);
  const draft = { ...opened, text: text.replace('before', 'after') };
  assert.equal(selectIsDirty({ context: { document: draft } }), true);
  assert.throws(() => editDocument(draft, ['metadata', 'custom', 'other'], 1, 'edit'), /Apply or discard/);
  const applied = applyText(draft, draft.text);
  assert.equal(applied.value.metadata.custom.marker, 'after');
  assert.equal(applied.value.metadata.custom.large, 9223372036854775807n);
  assert.equal(applied.past.length, 1);
  assert.equal(applied.appliedText, draft.text);
  const undone = undoDocument(applied);
  assert.equal(undone.text, text);
  assert.equal(undone.value.metadata.custom.marker, 'before');
  assert.equal(selectIsDirty({ context: { document: undone } }), false);
  assert.equal(redoDocument(undone).text, draft.text);
  const repaired = applyText(openDraftDocument('{ unfinished', 'draft'), '{}');
  assert.equal(repaired.text, '{}');
  assert.equal(repaired.appliedText, '{}');
  assert.equal(repaired.validation, 'pending');
});

test('state comparison preserves bigint precision, cell identity and text', () => {
  const cells = [{ key: 'x', value: 9223372036854775807n, text: 'one' }];
  assert.equal(sameCells(cells, [{ ...cells[0] }]), true);
  assert.equal(sameCells(cells, [{ ...cells[0], value: 9223372036854775806n }]), false);
  assert.equal(sameCells(cells, [{ ...cells[0], key: 'y' }]), false);
  assert.equal(sameCells(cells, [{ ...cells[0], text: 'two' }]), false);
});

test('an unreadable draft library is reported without discarding it or crashing the editor', () => {
  for (const raw of ['{ broken', '{"broken":{"id":"broken"}}']) {
    let writes = 0;
    const store = new LocalDraftStore({ getItem: () => raw, setItem: () => writes++ });
    const listing = readDraftListing(store);
    assert.deepEqual(listing.drafts, []);
    assert.match(listing.error, /unreadable/);
    assert.throws(() => store.save('new', 'New', 'new', '{}', 'save'), /unreadable/);
    assert.equal(writes, 0);
  }
  const store = new LocalDraftStore({ getItem: () => null, setItem: () => assert.fail('oversized draft was persisted') });
  assert.throws(() => store.save('large', 'Large', 'large', 'x'.repeat(2 * 1024 * 1024 + 1), 'save'), /byte cap/);
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
const fixture = JSON.stringify({ schema: 'puck.world.def.v1', documentId: 'cleanup-fixture', state: { world: [{ name: 'counter', kind: 'Int', value: 0 }] } });
let engine;
before(async () => { if (hasBundle) engine = await bootEngineFromLocalBundle(bundle); });
const native = (name, run) => test(name, { skip: hasBundle ? false : 'Publish Puck.World.Browser to run the real-engine cleanup regressions.' }, run);

async function start(engineOverride = engine) {
  // Only the official-content load is bypassed. Every parse, compile, write, tick and hash is
  // the published engine; wrappers in individual tests count or delay those same real calls.
  const machine = studioMachine.provide({ actors: { bootActor: fromPromise(async () => ({
    officialLoad: {}, engine: engineOverride, version: await engineOverride.version(),
  })) } });
  const entries = new Map();
  const input = { draftStore: new LocalDraftStore({ getItem: key => entries.get(key) ?? null, setItem: (key, value) => entries.set(key, value) }) };
  const actor = createActor(machine, { input }).start();
  await waitFor(actor, s => s.matches('ready'), { timeout: 5000 });
  actor.send({ type: 'OPEN_TEXT', name: 'cleanup', text: fixture });
  await idle(actor);
  assert.equal(actor.getSnapshot().context.document.validation, 'clean');
  return { actor, input };
}
const idle = actor => waitFor(actor, s => s.matches({ ready: { document: 'idle' } }), { timeout: 10000 });
async function previewEvent(actor, event) {
  actor.send(event);
  return (await waitFor(actor, s => s.matches({ ready: { preview: 'ready' } }), { timeout: 10000 })).context.preview;
}

native('rapid independent edits all commit while validation is coalesced', async t => {
  let parses = 0;
  const { actor } = await start({ ...engine, parse: async text => { parses++; return engine.parse(text); } });
  t.after(() => actor.stop());
  parses = 0;
  for (let i = 0; i < 12; i++) actor.send({ type: 'EDIT_DOCUMENT', path: ['metadata', 'custom', 'field' + i], value: i, label: 'field ' + i });
  assert.equal(Object.keys(actor.getSnapshot().context.document.value.metadata.custom).length, 12);
  assert.equal(actor.getSnapshot().context.document.past.length, 12);
  await idle(actor);
  assert.equal(parses, 1);
});

native('typing during validation is retained and preview compiles applied text only', async t => {
  const { actor } = await start();
  t.after(() => actor.stop());
  actor.send({ type: 'EDIT_DOCUMENT', path: ['metadata', 'custom', 'note'], value: 'applied', label: 'note' });
  actor.send({ type: 'SET_TEXT_DRAFT', text: '{ unfinished draft' });
  await idle(actor);
  assert.equal(actor.getSnapshot().context.document.text, '{ unfinished draft');
  const preview = await previewEvent(actor, { type: 'PREVIEW_START' });
  assert.equal(preview.rows[0].cells[0].value, 0n);
  actor.send({ type: 'SAVE_DRAFT', id: 'unfinished' });
  await idle(actor);
  assert.equal(selectIsDirty(actor.getSnapshot()), false);
  actor.send({ type: 'OPEN_TEXT', text: fixture });
  await idle(actor);
  actor.send({ type: 'LOAD_DRAFT', id: 'unfinished' });
  await idle(actor);
  assert.equal(actor.getSnapshot().context.document.text, '{ unfinished draft');
  assert.equal(actor.getSnapshot().context.document.validation, 'refused');
  actor.send({ type: 'APPLY_TEXT', text: fixture });
  await idle(actor);
  assert.equal(actor.getSnapshot().context.document.validation, 'clean');
});

native('history replays the selected tick and a new write discards the abandoned future', async t => {
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
  assert.equal(current.rows[0].cells[0].value, 3n);
  current = await previewEvent(actor, { type: 'PREVIEW_REDO' });
  assert.equal(current.tick, 2n);
  assert.equal(current.rows[0].cells[0].value, 7n);
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
  assert.equal(current.rows[0].cells[0].value, 9n);
});

native('cancelling compilation releases the handle when the engine eventually answers', async () => {
  let resume, compiledHandle;
  const pending = new Promise(resolve => { resume = resolve; });
  const controller = new AbortController();
  const wrapped = { ...engine, compile: async source => { const result = await engine.compile(source); compiledHandle = result.handle; await pending; return result; } };
  const compiling = compilePreview(wrapped, fixture, controller.signal);
  controller.abort();
  resume();
  await assert.rejects(compiling, /abort/i);
  await assert.rejects(engine.stateHash(compiledHandle));
});

native('stopping the actor releases its accepted preview handle', async () => {
  const { actor } = await start();
  const preview = await previewEvent(actor, { type: 'PREVIEW_START' });
  actor.stop();
  await new Promise(resolve => setImmediate(resolve));
  await assert.rejects(engine.stateHash(preview.handle));
});

native('unchanged topology geometry reuses the real engine answer and cell array identity', async () => {
  let calls = 0;
  const wrapped = { ...engine, cells: async json => { calls++; return engine.cells(json); } };
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
