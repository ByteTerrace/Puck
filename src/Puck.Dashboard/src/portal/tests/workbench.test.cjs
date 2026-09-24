// The Spatial workbench (`WorldWorkbench.tsx`) over the newest clean composition: a real `studioMachine` actor drives
// the fixture workspace with fake engines until the island check composes and the world engine lays the topology
// out, then the workbench renders from the actor's persisted snapshot, outside React.
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
const { WorldWorkbench } = require('../src/components/world/WorldWorkbench.tsx');
const { StateMatrixView } = require('../src/components/world/StateMatrixView.tsx');

async function composed(select) {
  const fixture = await buildOfficialFixture();
  const language = fakeEngine();
  const world = fakeEngine();
  const input = {
    official: fixture.official, engineMode: 'inline', fetchImpl: fixture.fetchImpl,
    bootEngine: bootSequence(language, world).bootEngine, draftStore: new LocalDraftStore(memoryStorage()),
  };
  const actor = createActor(studioMachine, { input }).start();
  await waitFor(actor, (s) => s.matches('ready'), { timeout: 5000 });
  actor.send({ type: 'OPEN_OFFICIAL', name: 'counter' });
  await waitFor(actor, (s) => s.matches({ ready: { workspace: 'open' } }), { timeout: 5000 });
  language.publish('counter.puck', 0, []);
  await waitFor(actor, (s) => Object.hasOwn(s.context.geometry, 'board'), { timeout: 5000 });
  select(actor);
  const snapshot = actor.getPersistedSnapshot();
  actor.stop();
  return { input, snapshot, world };
}

function render(component, { input, snapshot }) {
  return renderToStaticMarkup(React.createElement(MantineProvider, null,
    React.createElement(StudioContext.Provider, { options: { input, snapshot } }, component)));
}

test('the workbench lists the composed topology, its bound state, and the selection, from the world engine\'s geometry', async () => {
  const state = await composed((actor) => {
    assert.equal(actor.getSnapshot().context.selection.topology, 'board', 'the first laid-out topology is selected');
    actor.send({ type: 'SELECT_CELLS', ordinals: [1], mode: 'replace' });
  });
  assert.equal(state.world.calls.cells, 1, 'the world engine answered the layout once');
  const html = render(React.createElement(WorldWorkbench), state);
  assert.ok(html.includes('Document explorer'));
  assert.ok(html.includes('>board<'));
  assert.ok(html.includes('>marks<'), 'the cellsOf row bound to the board');
  assert.ok(html.includes('1 cell selected'));
  assert.ok(html.includes('Value: 2'), 'the authored cell value from the composition');
  assert.ok(html.includes('Preview write'));
  assert.ok(!html.includes('Paint authored cells'), 'the workbench writes no source');
});

test('with no selection the inspector prompts for one', async () => {
  const state = await composed(() => {});
  const html = render(React.createElement(WorldWorkbench), state);
  assert.ok(html.includes('Select a cell'));
});

test('the State view lists the composition\'s registers', async () => {
  const state = await composed(() => {});
  const html = render(React.createElement(StateMatrixView), state);
  assert.ok(html.includes('>score<'));
  assert.ok(html.includes('Composed state'));
});
