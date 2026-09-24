// The studio machine end to end on the published engine: the fixture workspace served as an official build, the
// language server pumped through the real channel, the island check compiling and composing on the real engine,
// and a preview ticking the result. Under Node the inline host keeps one .NET runtime per process, so the language
// engine and the world engine are the same runtime here; in the browser they are two workers.
//
// The editor normally initializes the language server and opens the file; with no editor, this test sends those
// messages itself, exactly as lsp-client would.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { buildOfficialFixture, bootSequence, memoryStorage, readWorkspaceFixture } = require('./support/studioFixture.cjs');

const { createActor, waitFor } = require('xstate');
const { studioMachine } = require('../src/machines/studioMachine.ts');
const { selectProblems } = require('../src/machines/studio/selectors.ts');
const { bootEngineFromLocalBundle } = require('../src/native/engineBoot.ts');
const { EngineCapabilityMissing } = require('../src/native/engineTypes.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');
const { sourceUri } = require('../src/document/sourcePaths.ts');

const bundle = path.resolve(__dirname, '../../../../Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle');

if (!fs.existsSync(path.join(bundle, 'main.mjs'))) {
  test(`studioMachine (SKIPPED: no AppBundle at ${bundle} — run 'dotnet publish src/Puck.World.Browser -c Release')`, { skip: true }, () => {});
} else {
  const engineReady = bootEngineFromLocalBundle(bundle);

  async function needsSourceExports(t, engine) {
    try {
      await engine.mountSources({});
      return true;
    } catch (error) {
      if (!(error instanceof EngineCapabilityMissing)) throw error;
      t.skip(`this AppBundle predates the source exports: ${error.message}`);
      return false;
    }
  }

  test('a source edits, diagnoses, checks, composes and previews on the real engine', { timeout: 120_000 }, async (t) => {
    const engine = await engineReady;
    if (!(await needsSourceExports(t, engine))) return;
    const fixture = await buildOfficialFixture();
    const input = {
      official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl,
      bootEngine: bootSequence(engine, engine).bootEngine, draftStore: new LocalDraftStore(memoryStorage()),
    };
    const actor = createActor(studioMachine, { input }).start();
    t.after(() => actor.stop());
    await waitFor(actor, (s) => s.matches('ready'), { timeout: 30_000 });
    assert.equal(actor.getSnapshot().context.boot.status, 'ready');

    actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
    await waitFor(actor, (s) => s.matches({ ready: { workspace: 'open' } }), { timeout: 30_000 });
    assert.deepEqual(actor.getSnapshot().context.refusals, []);

    const channel = actor.getSnapshot().context.languageChannel;
    const uri = sourceUri('counter.puck');
    const text = readWorkspaceFixture()['counter.puck'];
    channel.send(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { capabilities: {}, initializationOptions: { diagnostics: 'source' } } }));
    channel.send(JSON.stringify({ jsonrpc: '2.0', method: 'initialized', params: {} }));
    channel.send(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri, languageId: 'puck', version: 0, text } } }));

    const checked = await waitFor(actor, (s) => s.context.workspace.island?.revision === 0, { timeout: 60_000 });
    assert.deepEqual(selectProblems(checked).filter((problem) => problem.severity === 'error'), []);
    assert.equal(checked.context.workspace.island.ok, true, JSON.stringify(checked.context.workspace.island.composeDiagnostics));
    assert.equal(checked.context.compiled.path, 'counter.puck', 'the check\'s compile is the compiled view');
    assert.ok(Object.values(checked.context.compiled.sourceMap).some((span) => span.path === 'counter.puck'));

    actor.send({ type: 'PREVIEW_START' });
    await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 60_000 });
    actor.send({ type: 'PREVIEW_TICK' });
    const ticked = await waitFor(actor, (s) => s.context.preview.snapshots.length === 2 && s.matches({ ready: { preview: 'ready' } }), { timeout: 60_000 });
    const score = ticked.context.preview.rows.find((row) => row.name === 'score');
    assert.equal(score.cells[0].value.value, 1n, 'the rule ran once on the first tick');
    assert.ok(ticked.context.preview.snapshots[1].trace.rules.some((rule) => rule.name === 'score-up'));

    // A container never takes a colon: the source tier reports it at the version it diagnosed, and the preview stops.
    const broken = text.replace('state {', 'state: {');
    actor.send({ type: 'SOURCE_CHANGED', path: 'counter.puck', version: 1, text: broken });
    assert.equal(actor.getSnapshot().context.preview.status, 'idle');
    channel.send(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didChange', params: { textDocument: { uri, version: 1 }, contentChanges: [{ text: broken }] } }));
    const diagnosed = await waitFor(actor, (s) => s.context.workspace.diagnostics['counter.puck']?.version === 1, { timeout: 60_000 });
    const error = selectProblems(diagnosed).find((problem) => problem.code === 'PUCK040');
    assert.ok(error, JSON.stringify(selectProblems(diagnosed)));
    assert.equal(error.line, 5);
    actor.send({ type: 'PREVIEW_START' });
    assert.match(actor.getSnapshot().context.preview.refusal, /errors/);
  });
}
