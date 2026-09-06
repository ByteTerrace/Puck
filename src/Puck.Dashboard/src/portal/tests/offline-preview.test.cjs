const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

// Exercise the shipped TypeScript through Node's test runner, without a second bundler.
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);
const { createActor } = require('xstate');
const { worldSimulationMachine } = require('../src/machines/worldSimulationMachine.ts');
const { evaluatePuckExpression, getTopologyCoordinates, boardShift } = require('../src/engine/evaluator.ts');
const { inspectWorldDocument } = require('../src/engine/documentValidation.ts');
const { executeSimulationTick } = require('../src/engine/tickRunner.ts');
const { runWorldScenario, STANDARD_WORLD_SCENARIOS } = require('../src/engine/scenarioRunner.ts');
const { TIC_TAC_TOE_WORLD, AVAILABLE_PRESETS } = require('../src/catalog/worldCatalog.ts');
const { WorldStorageClient } = require('../src/clients/worldStorageClient.ts');
const topologyMap = Object.fromEntries(TIC_TAC_TOE_WORLD.state.lattices.map(t => [t.name, t]));

test('integer expressions preserve the top bit and refuse JavaScript or unbounded work', () => {
  const run = expression => evaluatePuckExpression(expression, {}, {}, {});
  assert.equal(run('1 << 63'), 1n << 63n);
  assert.equal(run('9007199254740993 - 9007199254740992'), 1);
  assert.equal(run('0 ? 1 / 0 : 7 / 2'), 3);
  for (const source of ['globalThis.alert(1)', '(()=>1)()', '1 << 64', '1 / 0', '('.repeat(100) + '1' + ')'.repeat(100)]) {
    assert.throws(() => run(source));
  }
});

test('board masks include the authored range and rings wrap', () => {
  const ring = { name: 'ring', $type: 'ring', width: 4, directions: [{ name: 'next', x: 1, y: 0, z: 0 }] };
  assert.equal(boardShift(8n, ring, 'next'), 1n);
  assert.equal(evaluatePuckExpression('$board:mask:board:1:2', {}, { board: { 0: 1, 2: 2, 3: 3 } }, { ring }), 5);
  assert.equal(evaluatePuckExpression('$board:mask:board:0:0', {}, { board: {} }, { ring }), 15);
});

test('geometry uses native hex ordinals, generated box layers, and bounded allocation', () => {
  const box = getTopologyCoordinates(TIC_TAC_TOE_WORLD.state.lattices[0]);
  assert.deepEqual(box[63], { x: 3, y: 3, z: 3 });
  assert.deepEqual(getTopologyCoordinates({ name: 'h', $type: 'hex', radius: 1 }), [
    { x: 0, y: 0, z: 0 }, { x: 1, y: 0, z: 0 }, { x: 1, y: 1, z: 0 },
    { x: 0, y: 1, z: 0 }, { x: -1, y: 0, z: 0 }, { x: -1, y: -1, z: 0 }, { x: 0, y: -1, z: 0 },
  ]);
  assert.throws(() => getTopologyCoordinates({ name: 'huge', $type: 'box', width: 4096, depth: 4096, layers: 4096 }));
});

test('rules execute once in document order with persistent Edge latches and immutable gate evidence', () => {
  const rules = [
    { name: 'level', mode: 'Level', effects: [{ $type: 'addState', state: 'count', value: 1 }] },
    { name: 'edge', mode: 'Edge', gate: { $type: 'compareState', state: 'count', comparison: 'Greater', value: 0 }, effects: [{ $type: 'addState', state: 'edges', value: 1 }] },
  ];
  const boards = { board: { 0: 1 } };
  const first = executeSimulationTick(1, 'tick', {}, { count: 0, edges: 0 }, boards, rules, {});
  const second = executeSimulationTick(2, 'tick', {}, first.nextState, first.nextBoardCells, rules, {}, first.nextEdgeLatches);
  assert.equal(second.nextState.count, 2);
  assert.equal(second.nextState.edges, 1);
  assert.equal(first.trace.ruleEvents[0].gateState.count, 0);
  assert.equal(first.trace.ruleEvents[1].gateState.count, 1);
  assert.strictEqual(first.nextBoardCells, boards);
  assert.deepEqual(first.nextState, { count: 1, edges: 1 });
});

test('all bundled Qubic scenarios pass, including cell 63 and occupied-cell refusal', () => {
  for (const scenario of STANDARD_WORLD_SCENARIOS.tictactoe) {
    const result = runWorldScenario(scenario, TIC_TAC_TOE_WORLD.rules, topologyMap);
    assert.equal(result.passed, true, result.errors.join('\n'));
    assert.equal(result.ticksExecuted, scenario.moves.length * 2);
  }
});

test('document intake distinguishes structural rejection from unsupported preview', () => {
  for (const preset of AVAILABLE_PRESETS) assert.deepEqual(inspectWorldDocument(JSON.stringify(preset.world)).previewIssues, []);
  assert.throws(() => inspectWorldDocument('{"state":'));
  assert.throws(() => inspectWorldDocument('{"state":{"world":[]},"metadata":{"value":9007199254740993}}'), /cannot preserve exactly/);
  const world = structuredClone(TIC_TAC_TOE_WORLD);
  world.rules[0].forEach = 'players';
  assert.match(inspectWorldDocument(JSON.stringify(world)).previewIssues.join(), /Bound rules/);
  world.state.world.push({ name: '__proto__', kind: 'int', value: 0 });
  assert.throws(() => inspectWorldDocument(JSON.stringify(world)), /safe identifiers/);
});

test('hover does no simulation work, bad loads retain history, and editing after undo works', () => {
  const actor = createActor(worldSimulationMachine).start();
  const original = actor.getSnapshot().context.history;
  for (let i = 0; i < 1000; i++) actor.send({ type: 'HOVER_CELL', cellIdx: i % 64 });
  assert.strictEqual(actor.getSnapshot().context.history, original);
  actor.send({ type: 'LOAD_WORLD', worldJsonText: '{' });
  assert.strictEqual(actor.getSnapshot().context.history, original);
  assert.ok(actor.getSnapshot().context.error);
  actor.send({ type: 'PREVIEW_CELL', cellIdx: 0 });
  actor.send({ type: 'PREVIEW_CELL', cellIdx: 1 });
  actor.send({ type: 'UNDO' });
  actor.send({ type: 'STATE_CHANGE', stateName: 'tttMoveCount', newValue: 4 });
  const context = actor.getSnapshot().context;
  assert.equal(context.error, null);
  assert.equal(context.history.at(-1).state.tttMoveCount, 4);
  assert.equal(context.history.at(-1).boardCells.tttBoard[1], undefined);
  actor.stop();
});

test('local saves round-trip over preset IDs, keep recoverable revisions, and report quota or corruption', async () => {
  const data = new Map();
  global.localStorage = { getItem: key => data.get(key) ?? null, setItem: (key, value) => data.set(key, value) };
  const client = new WorldStorageClient();
  const world = structuredClone(TIC_TAC_TOE_WORLD);
  world.metadata = { title: 'My edited example' };
  const first = await client.saveWorld('tictactoe', world);
  assert.equal(first.metadata.revision, 1);
  assert.equal((await client.loadWorld('tictactoe')).world.metadata.title, 'My edited example');
  world.metadata.title = 'Second revision';
  await client.saveWorld('tictactoe', world);
  assert.equal(client.listCheckpoints('tictactoe')[1].world.metadata.title, 'My edited example');
  const before = data.get('byteterrace.puck.worldVault.v1');
  localStorage.setItem = () => { throw new Error('quota'); };
  await assert.rejects(client.saveWorld('tictactoe', world), /Could not save/);
  assert.equal(data.get('byteterrace.puck.worldVault.v1'), before);
  data.set('byteterrace.puck.worldVault.v1', '{broken');
  await assert.rejects(client.saveWorld('tictactoe', world));
});

const { paintCells, validSelection } = require('../src/authoring/documentTools.ts');
const { projectTopology, projectRelationships } = require('../src/authoring/sceneProjection.ts');
const { appearanceFor, bindAppearance, readPresentation } = require('../src/authoring/presentation.ts');
const { findJsonRange } = require('../src/authoring/jsonReference.ts');
const fixtures = [
  { name: 'volume', $type: 'box', width: 3, depth: 2, layers: 5, cellSize: 2, layerHeight: 3, origin: [20, -8, 6] },
  { name: 'plane', $type: 'grid', width: 7, depth: 3 },
  { name: 'honeycomb', $type: 'hex', radius: 2 },
  { name: 'cycle', $type: 'ring', width: 11, directions: [{ name: 'next', x: 1, y: 0, z: 0 }] },
  { name: 'sparse', $type: 'lattice', coordinates: [{ x: -5, y: 2, z: 3 }, { x: 8, y: -2, z: 9 }, { x: 0, y: 0, z: 0 }] },
];
const genericWorld = () => ({ state: { lattices: fixtures, world: fixtures.map(t => ({ name: t.name + 'Values', kind: 'int', min: -10, max: 99, domain: { $type: 'cellsOf', topology: t.name }, cells: [] })) }, rules: [] });

test('scene projection preserves every address across topology families and layer separation', () => {
  for (const topology of fixtures) {
    const input = structuredClone(topology), logical = getTopologyCoordinates(topology);
    const first = projectTopology(topology), separated = projectTopology(topology, 3);
    assert.equal(first.cells.length, logical.length);
    assert.deepEqual(topology, input);
    first.cells.forEach((cell, index) => {
      assert.deepEqual(cell.ref, { topology: topology.name, index });
      assert.deepEqual(cell.coordinate, logical[index]);
      assert.deepEqual(separated.cells[index].ref, cell.ref);
      assert.deepEqual(separated.cells[index].coordinate, cell.coordinate);
      assert.ok(cell.position.every(Number.isFinite));
      assert.equal(separated.cells[index].position[0], cell.position[0]);
      assert.equal(separated.cells[index].position[2], cell.position[2]);
    });
  }
  const hex = projectTopology(fixtures[2]);
  for (let i = 1; i <= 6; i++) assert.ok(Math.abs(Math.hypot(...hex.cells[i].position.map((n, a) => n - hex.cells[0].position[a])) - 1) < 1e-9);
  const ring = projectTopology(fixtures[3]);
  assert.equal(projectRelationships(fixtures[3], ring).length, 11 * 6);
  assert.equal(new Set(ring.cells.map(c => c.position.join(','))).size, 11);
});

test('generic paint is atomic, exact, immutable and isolated to the chosen cell domain', () => {
  const world = genericWorld();
  for (const topology of fixtures) {
    const cells = getTopologyCoordinates(topology), selection = cells.map((_, index) => ({ topology: topology.name, index }));
    const next = paintCells(world, topology.name + 'Values', selection, -7);
    assert.equal(next.state.world.find(r => r.name === topology.name + 'Values').cells.length, cells.length);
    assert.strictEqual(next.state.lattices, world.state.lattices);
    assert.ok(world.state.world.every(r => r.cells.length === 0));
    assert.strictEqual(paintCells(next, topology.name + 'Values', selection, -7), next);
    assert.throws(() => paintCells(world, topology.name + 'Values', [...selection, { topology: topology.name, index: cells.length }], 4));
    assert.throws(() => paintCells(world, topology.name + 'Values', selection, 100));
    assert.throws(() => paintCells(world, topology.name + 'Values', selection, 1.2));
  }
  assert.throws(() => paintCells(world, 'volumeValues', [{ topology: 'plane', index: 0 }], 3));
  assert.deepEqual(validSelection(world, [{ topology: 'volume', index: 0 }, { topology: 'volume', index: 0 }, { topology: 'missing', index: 0 }]), [{ topology: 'volume', index: 0 }]);
});

test('authoring revisions preserve selection, keep preview separate and branch after undo', () => {
  const actor = createActor(worldSimulationMachine).start();
  actor.send({ type: 'LOAD_WORLD', worldJsonText: JSON.stringify(genericWorld(), null, 2) });
  const baseline = actor.getSnapshot().context;
  const selection = Array.from({ length: 20 }, (_, index) => ({ topology: 'volume', index }));
  actor.send({ type: 'SELECT_CELLS', selection });
  assert.strictEqual(actor.getSnapshot().context.history, baseline.history);
  assert.strictEqual(actor.getSnapshot().context.documentHistory, baseline.documentHistory);
  actor.send({ type: 'PAINT_CELLS', stateName: 'volumeValues', value: 7 });
  const painted = actor.getSnapshot().context;
  assert.equal(painted.documentHistory.length, 2);
  assert.equal(painted.documentIndex, 1);
  assert.equal(painted.parsedWorld.state.world[0].cells.length, 20);
  assert.equal(painted.history.length, 1);
  assert.strictEqual(painted.topologies, baseline.topologies);
  actor.send({ type: 'DOCUMENT_UNDO' });
  assert.equal(actor.getSnapshot().context.isDirty, false);
  assert.strictEqual(actor.getSnapshot().context.topologies, baseline.topologies);
  assert.equal(actor.getSnapshot().context.parsedWorld.state.world[0].cells.length, 0);
  assert.deepEqual(actor.getSnapshot().context.selection, selection);
  actor.send({ type: 'DOCUMENT_REDO' });
  assert.equal(actor.getSnapshot().context.parsedWorld.state.world[0].cells.length, 20);
  actor.send({ type: 'SET_DIRTY', isDirty: false });
  actor.send({ type: 'DOCUMENT_UNDO' });
  assert.equal(actor.getSnapshot().context.isDirty, true);
  actor.send({ type: 'DOCUMENT_REDO' });
  assert.equal(actor.getSnapshot().context.isDirty, false);
  actor.send({ type: 'DOCUMENT_UNDO' });
  actor.send({ type: 'PAINT_CELLS', stateName: 'volumeValues', value: 9 });
  assert.equal(actor.getSnapshot().context.documentHistory.length, 2);
  const branch = actor.getSnapshot().context;
  actor.send({ type: 'PAINT_CELLS', stateName: 'volumeValues', value: 500 });
  assert.strictEqual(actor.getSnapshot().context.documentHistory, branch.documentHistory);
  assert.strictEqual(actor.getSnapshot().context.history, branch.history);
  actor.send({ type: 'APPLY_DOCUMENT', worldJsonText: '{' });
  assert.strictEqual(actor.getSnapshot().context.parsedWorld, branch.parsedWorld);
  actor.send({ type: 'APPLY_DOCUMENT', worldJsonText: JSON.stringify(branch.parsedWorld) });
  assert.equal(actor.getSnapshot().context.worldJsonText, JSON.stringify(branch.parsedWorld));
  assert.equal(actor.getSnapshot().context.documentHistory.length, 3);
  actor.stop();
});

test('preview input and preview time travel never author cells or document revisions', () => {
  const actor = createActor(worldSimulationMachine).start();
  const start = actor.getSnapshot().context;
  actor.send({ type: 'SELECT_CELLS', selection: [{ topology: 'tttCube', index: 63 }] });
  actor.send({ type: 'PREVIEW_CELL', cellIdx: 63 });
  assert.equal(actor.getSnapshot().context.history.at(-1).boardCells.tttBoard[63], 1);
  assert.strictEqual(actor.getSnapshot().context.documentHistory, start.documentHistory);
  assert.equal(actor.getSnapshot().context.worldJsonText, start.worldJsonText);
  assert.equal(actor.getSnapshot().context.isDirty, false);
  actor.send({ type: 'UNDO' });
  assert.equal(actor.getSnapshot().context.historyIndex, 0);
  assert.equal(actor.getSnapshot().context.documentIndex, 0);
  actor.stop();
});

test('presentation metadata is declarative, validated and independent of game vocabulary', () => {
  const world = genericWorld();
  const styled = bindAppearance(world, 'volumeValues', -7, { label: 'Stone', color: '#aabbcc', shape: 'diamond', hidden: true });
  assert.deepEqual(appearanceFor(-7, readPresentation(styled).volumeValues), { label: 'Stone', color: '#aabbcc', shape: 'diamond', hidden: true });
  assert.equal(appearanceFor(57).label, '57');
  assert.strictEqual(styled.state, world.state);
  assert.deepEqual(inspectWorldDocument(JSON.stringify(styled)).previewIssues, []);
  assert.throws(() => bindAppearance(world, 'volumeValues', 1, { label: 'x', color: 'url(evil)' }));
  assert.throws(() => bindAppearance(world, 'volumeValues', 1, { label: 'x', color: '#ffffff', shape: 'game-piece' }));
  assert.throws(() => bindAppearance(world, 'missing', 1, { label: 'x', color: '#ffffff' }));
});

test('JSON reveal resolves addresses through reordered properties and escaped strings', () => {
  const world = { metadata: { name: 'same', text: 'quote: " and slash: ' + String.fromCharCode(92) }, state: { world: [{ cells: [{ value: -7, key: 3 }], name: 'same' }] } };
  const text = JSON.stringify(world, null, 4);
  const range = findJsonRange(text, ['state', 'world', 0, 'cells', 0, 'value']);
  assert.equal(text.slice(...range), '-7');
  assert.equal(findJsonRange(text, ['missing']), null);
  assert.equal(findJsonRange('{', []), null);
});

test('keyboard address ranges are bounded, deduplicated and independent of dimensionality', () => {
  const { parseCellAddresses } = require('../src/authoring/documentTools.ts');
  const box = fixtures[0];
  assert.equal(parseCellAddresses('0-19, 3, 29', box).length, 21);
  for (const invalid of ['', '-1', '3-1', '0-999999999999', '1.5', 'x']) assert.throws(() => parseCellAddresses(invalid, box));
});

test('document history is bounded and topology edits discard invalid addresses', () => {
  const actor = createActor(worldSimulationMachine).start();
  actor.send({ type: 'LOAD_WORLD', worldJsonText: JSON.stringify(genericWorld()) });
  actor.send({ type: 'SELECT_CELLS', selection: [{ topology: 'volume', index: 29 }] });
  for (let value = 1; value <= 70; value++) actor.send({ type: 'PAINT_CELLS', stateName: 'volumeValues', value });
  assert.equal(actor.getSnapshot().context.documentHistory.length, 64);
  // Changing geometry without updating its authored cells is rejected atomically.
  const before = actor.getSnapshot().context;
  actor.send({ type: 'SAVE_TOPOLOGY', topology: { ...fixtures[0], layers: 1 } });
  assert.strictEqual(actor.getSnapshot().context.documentHistory, before.documentHistory);
  const world = genericWorld(); world.state.lattices = [{ ...fixtures[0], layers: 1 }, ...fixtures.slice(1)];
  actor.send({ type: 'APPLY_DOCUMENT', worldJsonText: JSON.stringify(world) });
  assert.deepEqual(actor.getSnapshot().context.selection, []);
  actor.stop();
});
