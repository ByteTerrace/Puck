// The composed studio shell (`StudioShell.tsx` — header, alerts, drafts panel, tabs), rendered from a real
// `studioMachine` actor driven over the fixture workspace with fake engines, outside React; each render hydrates
// `StudioContext.Provider` from the actor's persisted snapshot. `StudioShell.tsx` never reads `import.meta.env`
// (that lives only in `WorldStudio.tsx`), so the input is built by hand.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { buildOfficialFixture, fakeEngine, bootSequence, memoryStorage } = require('./support/studioFixture.cjs');

const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MantineProvider } = require('@mantine/core');
const { createActor, waitFor } = require('xstate');

const { studioMachine } = require('../src/machines/studioMachine.ts');
const { LocalDraftStore } = require('../src/document/localDrafts.ts');
const { StudioContext } = require('../src/context/StudioContext.tsx');
const { StudioShellWithConfirmation, PreviewTab } = require('../src/components/world/StudioShell.tsx');
const { DraftsPanel } = require('../src/components/world/drafts/DraftsPanel.tsx');
const { WorldStudioAlerts } = require('../src/components/world/WorldStudioAlerts.tsx');
const { SectionsNavigator, CompiledTab } = require('../src/components/world/authoring/CompiledViews.tsx');
const { SourceEditor } = require('../src/components/world/authoring/SourceEditor.tsx');

async function driven(drive, { draftStore = new LocalDraftStore(memoryStorage()), worldRefusal = null, world = fakeEngine() } = {}) {
  const fixture = await buildOfficialFixture();
  const language = fakeEngine();
  const boot = bootSequence(language, world);
  const input = {
    official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl, draftStore,
    bootEngine: worldRefusal
      ? async (official, options) => { if (boot.booted.length > 0) throw new Error(worldRefusal); return boot.bootEngine(official, options); }
      : boot.bootEngine,
  };
  const actor = createActor(studioMachine, { input }).start();
  await waitFor(actor, (s) => s.matches('ready'), { timeout: 5000 });
  await drive(actor, language);
  const snapshot = actor.getPersistedSnapshot();
  actor.stop();
  return { input, snapshot };
}

const open = (name) => async (actor) => {
  actor.send({ type: 'OPEN_OFFICIAL', name });
  await waitFor(actor, (s) => s.matches({ ready: { workspace: 'open' } }), { timeout: 5000 });
};

function render(component, { input, snapshot }) {
  return renderToStaticMarkup(React.createElement(MantineProvider, null,
    React.createElement(StudioContext.Provider, { options: { input, snapshot } }, component)));
}

test('the shell opens on the Source tab with the workspace\'s files, the open file, and its status', async () => {
  const state = await driven(open('counter'));
  const html = render(React.createElement(StudioShellWithConfirmation), state);
  assert.ok(html.includes('>counter<'), 'the document\'s name is the masthead title');
  assert.ok(html.includes('>world<'), 'the role badge names the manifest role');
  assert.ok(html.includes('commit fixture'), 'the build commit is shown');
  assert.ok(html.includes('world engine dormant'), 'the world engine has not booted');
  assert.ok(html.includes('aria-label="Workspace files"'));
  for (const name of ['counter.puck', 'token.puck', 'island.world.json']) assert.ok(html.includes(name), `the tree lists ${name}`);
  assert.ok(html.includes('>games/<'), 'files are grouped by directory');
  assert.ok(html.includes('Checking…'), 'diagnostics are pending for the open file');
  assert.ok(html.includes('>checking<'), 'the masthead badge says so too');
  for (const tab of ['Source', 'Compiled', 'Sections', 'Spatial', 'State', 'Preview', 'Console']) assert.ok(html.includes(`>${tab}<`), `the ${tab} tab is offered`);
});

test('a JSON-only workspace file opens read-only with a plain notice', async () => {
  const state = await driven(async (actor) => {
    await open('island')(actor);
  });
  const html = render(React.createElement(SourceEditor), state);
  assert.ok(html.includes('Read-only: this document has no .puck source yet.'));
});

test('diagnostics reach the alerts with a way to reveal each one, and a clean revision says so', async () => {
  const broken = await driven(async (actor, language) => {
    await open('counter')(actor);
    language.publish('counter.puck', 0, [{ range: { start: { line: 12, character: 2 }, end: { line: 12, character: 7 } }, severity: 1, code: 'PUCK040', message: 'a container never takes a colon' }]);
  });
  const html = render(React.createElement(WorldStudioAlerts), broken);
  assert.ok(html.includes('1 error'));
  assert.ok(html.includes('counter.puck:13:3'), 'the 1-based position is shown');
  assert.ok(html.includes('PUCK040'));
  assert.ok(html.includes('aria-label="Reveal counter.puck line 13"'));

  const clean = await driven(async (actor, language) => {
    await open('counter')(actor);
    language.publish('counter.puck', 0, []);
    await waitFor(actor, (s) => s.context.workspace.island?.ok === true, { timeout: 5000 });
  });
  assert.ok(render(React.createElement(WorldStudioAlerts), clean).includes('No problems.'));

  const informed = await driven(async (actor, language) => {
    await open('counter')(actor);
    language.publish('counter.puck', 0, []);
    await waitFor(actor, (s) => s.context.workspace.island?.ok === true, { timeout: 5000 });
  }, { world: fakeEngine({ composeSource: () => ({ ok: true, composed: '{}', diagnostics: [{ code: 'DEFERRED', severity: 'information', message: 'validation is deferred', path: 'counter.puck', line: 0, column: 0, length: 0 }] }) }) });
  const informedHtml = render(React.createElement(WorldStudioAlerts), informed);
  assert.ok(informedHtml.includes('counter.puck (whole document)'), 'a line-0 finding is about the whole document, never a line');
  assert.ok(!informedHtml.includes('counter.puck:0:0'));
});

test('the Preview tab names why it cannot start, and renders the rule trace with a link to its source', async () => {
  const waiting = await driven(open('counter'));
  assert.ok(render(React.createElement(PreviewTab), waiting).includes('still being checked'));

  const ticked = await driven(async (actor, language) => {
    await open('counter')(actor);
    language.publish('counter.puck', 0, []);
    await waitFor(actor, (s) => s.context.workspace.island?.ok === true, { timeout: 5000 });
    actor.send({ type: 'REQUEST_COMPILED' });
    await waitFor(actor, (s) => s.context.compiled !== null, { timeout: 5000 });
    actor.send({ type: 'PREVIEW_START' });
    await waitFor(actor, (s) => s.matches({ ready: { preview: 'ready' } }), { timeout: 5000 });
    actor.send({ type: 'PREVIEW_TICK' });
    await waitFor(actor, (s) => s.context.preview.snapshots.length > 1, { timeout: 5000 });
  });
  const html = render(React.createElement(PreviewTab), ticked);
  assert.ok(html.includes('Rules visited'));
  assert.ok(html.includes('title="counter.puck:11:1"'), 'the rule links to the span its source map names');
});

test('a refused world engine disables the preview start and names the reason where a refused start shows', async () => {
  const state = await driven(async (actor, language) => {
    await open('counter')(actor);
    language.publish('counter.puck', 0, []);
    await waitFor(actor, (s) => s.matches({ ready: { world: 'refused' } }), { timeout: 5000 });
  }, { worldRefusal: 'the engine file did not verify.' });
  const html = render(React.createElement(PreviewTab), state);
  assert.match(html, /<button[^>]*disabled=""[^>]*>(?:(?!<\/button>).)*Start preview/s, 'the start button is disabled');
  assert.ok(html.includes('Preview refused'));
  assert.ok(html.includes('the world engine refused to boot: the engine file did not verify.'));
  assert.equal(html.split('the world engine refused to boot').length, 2, 'the reason shows once');
});

test('the Sections and Compiled views show the compiled IR with the pointer and span under the cursor', async () => {
  const state = await driven(async (actor) => {
    await open('counter')(actor);
    actor.send({ type: 'REQUEST_COMPILED' });
    await waitFor(actor, (s) => s.context.compiled !== null, { timeout: 5000 });
  });
  const sections = render(React.createElement(SectionsNavigator), state);
  assert.equal((sections.match(/data-section-key="/g) ?? []).length, 4, 'one entry per schema root section');
  assert.ok(sections.includes('>authored<'));
  assert.ok(sections.includes('>absent<'), 'the bundle names a section (cameras) this document does not author');
  assert.ok(sections.includes('Compiled counter.puck at revision 0.'));
  const compiled = render(React.createElement(CompiledTab), state);
  assert.ok(compiled.includes('Go to source'));
  assert.ok(compiled.includes('no source span'), 'the root pointer has no span in the fixture map');
});

test('the drafts panel lists a saved draft with its changed-file count', async () => {
  const draftStore = new LocalDraftStore(memoryStorage());
  draftStore.save('demo-draft', 'Demo draft', 'counter', { 'counter.puck': 'x', 'games/token.puck': 'y' }, '2 changed files');
  const state = await driven(async () => {}, { draftStore });
  const html = render(React.createElement(DraftsPanel), state);
  assert.ok(html.includes('Demo draft'));
  assert.ok(html.includes('>counter<'));
  assert.ok(html.includes('>2<'), 'the changed-file count');
});
