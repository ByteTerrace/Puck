// The studio machine with fake engines over the fixture workspace: what it accepts while booting, how an open mounts
// the workspace, how diagnostics and the island check gate a preview, which engine does what, and how drafts
// overlay the official build. Every count here is of engine calls, never of time.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { buildOfficialFixture, fakeEngine, bootSequence, memoryStorage } = require('./support/studioFixture.cjs');
const { createActor, fromPromise, waitFor } = require('xstate');
const { studioMachine } = require('../src/machines/studioMachine.ts');
const {
  selectCanDrivePreview, selectCanSaveDraft, selectCanStartPreview, selectChecking, selectEngineRefusal, selectIsDirty, selectPreviewBusy,
  selectProblems,
} = require('../src/machines/studio/selectors.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');

function deferred() {
  let resolve;
  const promise = new Promise((done) => { resolve = done; });
  return { promise, resolve };
}

async function start({ language = fakeEngine(), world = fakeEngine(), draftStore = new LocalDraftStore(memoryStorage()), machine = studioMachine } = {}) {
  const fixture = await buildOfficialFixture();
  const boot = bootSequence(language, world);
  const actor = createActor(machine, {
    input: { official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl, bootEngine: boot.bootEngine, draftStore },
  }).start();
  await waitFor(actor, (snapshot) => snapshot.matches('ready'), { timeout: 5000 });
  return { actor, language, world, boot, fixture, draftStore };
}

const opened = (actor) => waitFor(actor, (snapshot) => snapshot.matches({ ready: { workspace: 'open' } }), { timeout: 5000 });
const islandAt = (actor, revision) => waitFor(actor, (snapshot) => snapshot.context.workspace?.island?.revision === revision, { timeout: 5000 });
/** Waits, one macrotask at a time, for a condition no machine snapshot announces (an engine call starting). */
async function until(condition, turns = 1000) {
  for (let turn = 0; turn < turns; turn++) {
    if (condition()) return;
    await new Promise((resolve) => setImmediate(resolve));
  }
  throw new Error('condition never held');
}
const error = (line) =>({ range: { start: { line, character: 0 }, end: { line, character: 4 } }, severity: 1, code: 'PUCK002', message: 'broken' });

test('while booting the studio takes no preview or draft commands, and is not busy', () => {
  const actor = createActor(studioMachine.provide({ actors: { bootActor: fromPromise(() => new Promise(() => {})) } }), {
    input: { draftStore: new LocalDraftStore(memoryStorage()) },
  }).start();
  const booting = actor.getSnapshot();
  assert.equal(selectCanStartPreview(booting), false);
  assert.equal(selectCanSaveDraft(booting), false);
  assert.equal(selectPreviewBusy(booting), false);
  actor.stop();
});

test('opening a document mounts every source into the language engine and opens the document\'s own source', async () => {
  const { actor, language, boot, fixture } = await start();
  assert.equal(boot.booted.length, 1, 'only the language engine boots with the studio');
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  const snapshot = await opened(actor);
  const workspace = snapshot.context.workspace;
  assert.equal(workspace.entry, 'counter.puck');
  assert.equal(workspace.active, 'counter.puck');
  assert.equal(workspace.root, 'counter.puck', 'a root world composes itself');
  assert.deepEqual(Object.keys(workspace.files).sort(), Object.keys(fixture.files).sort());
  assert.equal(language.calls.mountSources, 1);
  assert.deepEqual([...language.mounted.keys()].sort(), Object.keys(fixture.files).sort());
  assert.equal(selectChecking(snapshot), true, 'diagnostics are pending for version 0');
  assert.equal(selectIsDirty(snapshot), false);
  actor.stop();
});

test('a fragment composes under the manifest\'s composed island root, found through composed[]', async () => {
  const { actor } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'games/token' });
  const workspace = (await opened(actor)).context.workspace;
  assert.equal(workspace.entry, 'games/token.puck');
  assert.equal(workspace.root, 'island.world.json');
  actor.stop();
});

test('clean diagnostics boot the world engine once, and the island check composes each clean revision once', async () => {
  const { actor, language, world, boot } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  assert.equal(boot.booted.length, 1, 'no world engine before a revision is ready');

  language.publish('counter.puck', 0, []);
  const checked = await islandAt(actor, 0);
  assert.equal(boot.booted.length, 2, 'the first ready revision boots the world engine');
  assert.equal(checked.context.workspace.island.ok, true);
  assert.equal(world.calls.mountSources, 1, 'the world engine receives the workspace before its first job');
  assert.equal(world.calls.composeSource, 1);
  assert.equal(language.calls.composeSource, 0, 'the language engine never composes');
  assert.equal(selectCanStartPreview(checked), true);

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'broken' });
  assert.equal(selectChecking(actor.getSnapshot()), true);
  language.publish('counter.puck', 1, [error(3)]);
  await waitFor(actor, (snapshot) => !selectChecking(snapshot));
  assert.equal(world.calls.composeSource, 1, 'a revision with errors is not composed');
  assert.equal(selectProblems(actor.getSnapshot()).length, 1);

  actor.send({ type: 'PREVIEW_START' });
  assert.match(actor.getSnapshot().context.preview.refusal, /errors/);

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 2, text: 'fixed' });
  language.publish('counter.puck', 2, []);
  await islandAt(actor, 2);
  assert.equal(world.calls.composeSource, 2);
  assert.equal(world.calls.writeSource, 1, 'only the changed file is written to the world engine');
  assert.equal(world.mounted.get('counter.puck'), 'fixed');
  assert.equal(boot.booted.length, 2, 'the world engine boots once');
  actor.stop();
});

test('the world engine boots from the language engine\'s compiled module', async () => {
  const language = fakeEngine();
  const compiled = { compiledModule: true };
  Object.defineProperty(language, 'wasmModule', { value: compiled, enumerable: false });
  const { actor, boot } = await start({ language });
  assert.equal(boot.options[0].wasmModule, undefined, 'the language engine compiles its own');
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  language.publish('counter.puck', 0, []);
  await islandAt(actor, 0);
  assert.equal(boot.options[1].wasmModule, compiled);
  actor.stop();
});

test('diagnostics for an older version never settle the current one', async () => {
  const { actor, language } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 3, text: 'three' });
  language.publish('counter.puck', 2, []);
  assert.equal(selectChecking(actor.getSnapshot()), true);
  language.publish('counter.puck', 3, []);
  assert.equal(selectChecking(actor.getSnapshot()), false);
  actor.stop();
});

test('an island check that finishes after a newer edit is dropped, and one compose runs at a time', async () => {
  const gates = [];
  let running = 0;
  let most = 0;
  const world = fakeEngine({
    composeSource: async () => {
      running++;
      most = Math.max(most, running);
      const gate = deferred();
      gates.push(gate);
      await gate.promise;
      running--;
      return { ok: true, composed: '{"schema":"puck.world.definition.v1"}', diagnostics: [] };
    },
  });
  const { actor, language } = await start({ world });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  language.publish('counter.puck', 0, []);
  await until(() => gates.length === 1);

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'one' });
  language.publish('counter.puck', 1, []);
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 2, text: 'two' });
  language.publish('counter.puck', 2, []);
  gates[0].resolve();
  await until(() => gates.length === 2);
  assert.equal(actor.getSnapshot().context.workspace.island, null, 'revision 0 finished stale and was dropped');
  gates[1].resolve();
  await islandAt(actor, 2);
  assert.equal(most, 1, 'never two compositions at once');
  assert.equal(gates.length, 2, 'revision 1 was superseded while it waited and never ran');
  actor.stop();
});

test('a compiling preview carries the busy tag, and a newer source change stops a running preview', async () => {
  const { actor, language, world } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  language.publish('counter.puck', 0, []);
  await islandAt(actor, 0);

  actor.send({ type: 'PREVIEW_START' });
  assert.equal(selectPreviewBusy(actor.getSnapshot()), true);
  const ready = await waitFor(actor, (snapshot) => snapshot.matches({ ready: { preview: 'ready' } }));
  assert.equal(selectCanDrivePreview(ready), true);
  assert.equal(world.calls.compile, 1, 'the preview compiles the island check\'s composition, not a second one');
  assert.equal(world.calls.composeSource, 1);

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'edited' });
  assert.equal(actor.getSnapshot().context.preview.status, 'idle');
  await until(() => world.calls.release === 1);
  actor.stop();
});

test('a source change for a file the workspace does not edit is not taken', async () => {
  const { actor } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  const before = actor.getSnapshot().context.workspace.revision;
  actor.send({ type: 'SOURCE_CHANGED', path: 'island.world.json', version: 1, text: '{}' });
  actor.send({ type: 'SOURCE_CHANGED', path: 'absent.puck', version: 1, text: 'x' });
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 0, text: 'stale version' });
  assert.equal(actor.getSnapshot().context.workspace.revision, before);
  actor.stop();
});

test('a draft saves only the changed files and reopens as an overlay on the official build', async () => {
  const { actor, draftStore, fixture } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'changed text' });
  assert.equal(selectIsDirty(actor.getSnapshot()), true);

  actor.send({ type: 'SAVE_DRAFT', title: 'My counter' });
  assert.equal(selectCanSaveDraft(actor.getSnapshot()), false, 'one library write at a time');
  const saved = await waitFor(actor, (snapshot) => snapshot.matches({ ready: { library: 'idle' } }));
  assert.equal(selectIsDirty(saved), false);
  assert.equal(saved.context.workspace.draftId, 'my-counter');
  const draft = draftStore.load('my-counter');
  assert.deepEqual(draft.revisions[0].files, { 'counter.puck': 'changed text' });
  assert.equal(draft.documentName, 'counter');

  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await waitFor(actor, (snapshot) => snapshot.context.workspace?.id === 2 && snapshot.matches({ ready: { workspace: 'open' } }));
  assert.equal(actor.getSnapshot().context.workspace.files['counter.puck'].text, fixture.files['counter.puck']);

  actor.send({ type: 'LOAD_DRAFT', id: 'my-counter' });
  const loaded = await waitFor(actor, (snapshot) => snapshot.context.workspace?.id === 3 && snapshot.matches({ ready: { workspace: 'open' } }));
  const file = loaded.context.workspace.files['counter.puck'];
  assert.equal(file.text, 'changed text');
  assert.equal(file.officialText, fixture.files['counter.puck']);
  assert.equal(selectIsDirty(loaded), false, 'a loaded draft is its own saved state');

  actor.send({ type: 'DELETE_DRAFT', id: 'my-counter' });
  const deleted = await waitFor(actor, (snapshot) => snapshot.matches({ ready: { library: 'idle' } }) && snapshot.context.drafts.drafts.length === 0);
  assert.equal(deleted.context.drafts.error, null);
  actor.stop();
});

test('a newer open replaces one still reading, and a missing draft is a studio refusal that keeps the workspace', async () => {
  const { actor } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'games/token' });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  const first = await opened(actor);
  assert.equal(first.context.workspace.documentName, 'counter');

  actor.send({ type: 'LOAD_DRAFT', id: 'absent' });
  const missing = await waitFor(actor, (snapshot) => snapshot.context.refusals.length > 0);
  assert.match(missing.context.refusals[0], /no local draft named 'absent'/);
  await opened(actor);
  assert.equal(actor.getSnapshot().context.workspace.documentName, 'counter');
  actor.stop();
});

test('a language engine without the source exports still opens the workspace, and says why it has no diagnostics', async () => {
  const language = fakeEngine();
  language.mountSources = async () => { throw new Error("this engine build has no 'MountSources' export."); };
  const { actor } = await start({ language });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  const snapshot = await opened(actor);
  assert.equal(snapshot.context.workspace.entry, 'counter.puck');
  assert.match(snapshot.context.refusals[0], /MountSources/);
  actor.stop();
});

test('the compiled view is fetched on request, from the language engine, and reused until the revision moves', async () => {
  const { actor, language, world } = await start();
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  actor.send({ type: 'REQUEST_COMPILED' });
  await waitFor(actor, (snapshot) => snapshot.context.compiled !== null);
  actor.send({ type: 'REQUEST_COMPILED' });
  assert.equal(language.calls.compileSource, 1, 'a second request for the same revision compiles nothing');
  assert.equal(world.calls.compileSource, 0);

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'edited' });
  assert.equal(language.calls.compileSource, 1, 'an edit never compiles on its own');
  actor.send({ type: 'REQUEST_COMPILED' });
  await waitFor(actor, (snapshot) => snapshot.context.compiled?.revision === 1);
  assert.equal(language.calls.compileSource, 2);
  assert.equal(language.mounted.get('counter.puck'), 'edited', 'the buffer is written before the compile');
  actor.stop();
});

test('the world engine\'s semantic tier joins the source tier, blocks composition on an error, and serves the compiled view', async () => {
  const semantic = { code: 'PUCK_LINT_003', severity: 'error', message: 'no row named ghost', path: 'counter.puck', line: 14, column: 3, length: 5 };
  let broken = true;
  const world = fakeEngine({
    compileSource: (path) => ({
      ok: !broken, document: broken ? null : '{"schema":"puck.world.definition.v1","rules":[{"name":"score-up"}]}', worlds: [],
      diagnostics: broken ? [semantic] : [], sourceMap: { '/rules/0': { path, line: 12, column: 1, length: 4, module: null } },
    }),
  });
  const { actor, language } = await start({ world });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  const warning = { range: { start: { line: 2, character: 0 }, end: { line: 2, character: 4 } }, severity: 2, code: 'PUCK_LINT_001', message: 'unused' };
  language.publish('counter.puck', 0, [warning]);
  const checked = await islandAt(actor, 0);
  assert.equal(world.calls.compileSource, 1, 'the world engine compiles the open source once per revision');
  assert.equal(world.calls.composeSource, 0, 'a semantic error skips the composition');
  assert.equal(checked.context.workspace.island.ok, false);
  const { editorDiagnostics } = require('../src/machines/studio/workspace.ts');
  assert.deepEqual(editorDiagnostics(checked.context.workspace, 'counter.puck').map((d) => d.code), ['PUCK_LINT_001', 'PUCK_LINT_003'], 'the editor shows both tiers');
  assert.equal(selectProblems(checked).length, 2);
  actor.send({ type: 'PREVIEW_START' });
  assert.match(actor.getSnapshot().context.preview.refusal, /found errors/);

  broken = false;
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'fixed' });
  assert.equal(editorDiagnostics(actor.getSnapshot().context.workspace, 'counter.puck'), null, 'nothing new to show until the source tier answers');
  language.publish('counter.puck', 1, []);
  const clean = await islandAt(actor, 1);
  assert.equal(clean.context.workspace.island.ok, true);
  assert.equal(world.calls.composeSource, 1);
  assert.equal(clean.context.compiled.revision, 1, 'the check\'s compile is the compiled view');
  actor.send({ type: 'REQUEST_COMPILED' });
  assert.equal(language.calls.compileSource, 0, 'so a compiled view request at this revision compiles nothing');
  actor.stop();
});

/** A boot seam that boots `language`, then refuses every later boot (the world engine's) with `reason`. */
function refusingWorldBoot(language, reason) {
  let boots = 0;
  return { bootEngine: async () => { boots++; if (boots === 1) return language; throw new Error(reason); }, boots: () => boots };
}

test('a world engine whose boot is refused leaves editing working and refuses the preview with its reason', async () => {
  const language = fakeEngine();
  const boot = refusingWorldBoot(language, "engine file '_framework/dotnet.native.wasm' does not match its manifest hash.");
  const fixture = await buildOfficialFixture();
  const actor = createActor(studioMachine, {
    input: { official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl, bootEngine: boot.bootEngine, draftStore: new LocalDraftStore(memoryStorage()) },
  }).start();
  await waitFor(actor, (snapshot) => snapshot.matches('ready'), { timeout: 5000 });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  language.publish('counter.puck', 0, []);
  const refused = await waitFor(actor, (snapshot) => snapshot.matches({ ready: { world: 'refused' } }));
  assert.equal(boot.boots(), 2);
  assert.match(refused.context.world.refusal, /dotnet\.native\.wasm/);
  assert.equal(selectCanStartPreview(refused), false, 'a start known to fail is not taken, from the moment it is known');
  assert.match(selectEngineRefusal(refused), /the world engine refused to boot: .*dotnet\.native\.wasm/);
  assert.equal(refused.context.workspace.island, null, 'no check runs without a world engine');

  assert.equal(refused.can({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'edited' }), true);
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'edited' });
  language.publish('counter.puck', 1, [error(2)]);
  const edited = await waitFor(actor, (snapshot) => !selectChecking(snapshot));
  assert.equal(edited.context.workspace.files['counter.puck'].text, 'edited');
  assert.equal(selectProblems(edited).length, 1, 'the language engine still diagnoses');
  assert.equal(selectCanSaveDraft(edited), true);
  language.publish('counter.puck', 1, []);
  await waitFor(actor, (snapshot) => selectProblems(snapshot).length === 0);

  assert.equal(selectCanStartPreview(actor.getSnapshot()), false, 'a clean source does not make it startable');
  actor.send({ type: 'PREVIEW_START' });
  const preview = actor.getSnapshot();
  assert.equal(preview.context.preview.status, 'idle', 'the start is not taken');
  assert.equal(preview.context.preview.refusal, null);
  assert.equal(selectCanDrivePreview(preview), false);
  assert.equal(boot.boots(), 2, 'a refused world engine is not booted again');
  actor.stop();
});

test('a language engine whose boot is refused still opens and edits the workspace, and refuses the preview with its reason', async () => {
  const fixture = await buildOfficialFixture();
  const boot = { bootEngine: async () => { throw new Error("engine file '_framework/dotnet.native.wasm' does not match its manifest hash."); } };
  const actor = createActor(studioMachine, {
    input: { official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl, bootEngine: boot.bootEngine, draftStore: new LocalDraftStore(memoryStorage()) },
  }).start();
  const booted = await waitFor(actor, (snapshot) => snapshot.matches('ready'), { timeout: 5000 });
  assert.equal(booted.context.boot.status, 'ready', 'the official build loaded');
  assert.equal(booted.context.boot.build.commit, 'fixture');
  assert.equal(booted.context.language.status, 'refused');
  assert.match(booted.context.language.refusal, /dotnet\.native\.wasm/);
  assert.equal(selectCanStartPreview(booted), false);
  assert.match(selectEngineRefusal(booted), /the language engine refused to boot: .*dotnet\.native\.wasm/);

  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  const open = await opened(actor);
  assert.equal(open.context.workspace.entry, 'counter.puck');
  assert.match(open.context.refusals[0], /language engine is not running/);
  assert.equal(selectChecking(open), false, 'no diagnostics are coming, so the file is not left checking');

  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'edited' });
  const edited = actor.getSnapshot();
  assert.equal(edited.context.workspace.files['counter.puck'].text, 'edited');
  assert.equal(selectIsDirty(edited), true);
  assert.equal(selectChecking(edited), false);
  assert.equal(selectCanSaveDraft(edited), true);
  assert.equal(edited.can({ type: 'REQUEST_COMPILED' }), false, 'the compiled view needs the language engine');

  assert.equal(selectCanStartPreview(edited), false);
  actor.send({ type: 'PREVIEW_START' });
  const preview = actor.getSnapshot();
  assert.equal(preview.context.preview.status, 'idle', 'the start is not taken');
  assert.equal(selectCanDrivePreview(preview), false);
  assert.equal(preview.context.world.status, 'dormant', 'nothing is checked, so the world engine never boots');

  actor.send({ type: 'SAVE_DRAFT', title: 'offline' });
  const saved = await waitFor(actor, (snapshot) => snapshot.matches({ ready: { library: 'idle' } }) && snapshot.context.workspace.draftId === 'offline');
  assert.equal(selectIsDirty(saved), false);
  actor.stop();
});

test('an official build that does not verify refuses the boot by name, and nothing opens', async () => {
  const fixture = await buildOfficialFixture();
  const tampered = fixture.official.objectUrl(fixture.manifest.worldSchemaBundle.path).href;
  const fetchImpl = (input) => ((typeof input === 'string' ? input : input.href) === tampered
    ? Promise.resolve({ ok: true, status: 200, arrayBuffer: async () => new TextEncoder().encode('{"tampered":true}').buffer })
    : fixture.fetchImpl(input));
  let boots = 0;
  const actor = createActor(studioMachine, {
    input: { official: fixture.official, engineMode: 'inline', fetchImpl, bootEngine: async () => { boots++; return fakeEngine(); }, draftStore: new LocalDraftStore(memoryStorage()) },
  }).start();
  const booted = await waitFor(actor, (snapshot) => snapshot.matches('ready'), { timeout: 5000 });
  assert.equal(booted.context.boot.status, 'refused');
  assert.match(booted.context.boot.refusal, new RegExp(fixture.manifest.worldSchemaBundle.path));
  assert.equal(booted.context.language.status, 'refused');
  assert.equal(boots, 0, 'no engine boots from an official build that did not verify');

  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  const refused = await waitFor(actor, (snapshot) => snapshot.context.refusals.length > 0);
  assert.match(refused.context.refusals[0], /official build has loaded/);
  assert.equal(selectCanStartPreview(actor.getSnapshot()), false);
  assert.match(selectEngineRefusal(actor.getSnapshot()), /the official build did not load: .*objects\//);
  assert.equal(selectCanSaveDraft(actor.getSnapshot()), false);
  actor.stop();
});

test('the composed world\'s validation joins the semantic tier: an information finding never blocks the preview, a whole-document error does', async () => {
  // The browser defers a machine-catalog check as information; the test keys off the severity, never the code.
  const deferredCheck = { code: 'DEFERRED', severity: 'information', message: 'validation is deferred because no machine catalog was supplied.', path: 'counter.puck', line: 0, column: 0, length: 0 };
  const invalid = { code: 'PUCK030', severity: 'error', message: "rules[1] duplicates an earlier rule's name.", path: 'counter.puck', line: 0, column: 0, length: 0 };
  let findings = [deferredCheck];
  const world = fakeEngine({
    composeSource: () => ({
      ok: !findings.some((finding) => finding.severity === 'error'), composed: '{"schema":"puck.world.definition.v1"}', diagnostics: findings,
    }),
  });
  const { actor, language } = await start({ world });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await opened(actor);
  language.publish('counter.puck', 0, []);
  const informed = await islandAt(actor, 0);
  assert.equal(informed.context.workspace.island.ok, true);
  assert.deepEqual(selectProblems(informed).map((problem) => problem.severity), ['information']);
  const { editorDiagnostics } = require('../src/machines/studio/workspace.ts');
  assert.deepEqual(editorDiagnostics(informed.context.workspace, 'counter.puck').map((d) => [d.severity, d.line]), [['information', 0]], 'the editor shows it as information');
  assert.equal(selectCanStartPreview(informed), true);
  actor.send({ type: 'PREVIEW_START' });
  await waitFor(actor, (snapshot) => snapshot.matches({ ready: { preview: 'ready' } }));

  findings = [deferredCheck, invalid];
  actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: 'duplicated' });
  language.publish('counter.puck', 1, []);
  const refused = await islandAt(actor, 1);
  assert.equal(refused.context.workspace.island.ok, false);
  assert.equal(refused.context.workspace.island.composed !== null, true, 'it composed, and the composed world is invalid');
  assert.deepEqual(selectProblems(refused).map((problem) => [problem.severity, problem.line]), [['information', 0], ['error', 0]], 'a whole-document finding on the root');
  actor.send({ type: 'PREVIEW_START' });
  assert.match(actor.getSnapshot().context.preview.refusal, /found errors/);
  actor.stop();
});
