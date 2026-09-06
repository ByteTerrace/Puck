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
  actor.send({ type: 'CELL_CLICK', cellIdx: 0 });
  actor.send({ type: 'CELL_CLICK', cellIdx: 1 });
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
