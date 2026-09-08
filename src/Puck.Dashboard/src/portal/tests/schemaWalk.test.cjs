const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const { readSchemaBundle } = require('../scripts/puckCli.cjs');
const ts = require('typescript');

// Exercise the shipped TypeScript through Node's test runner, without a second bundler
// (the same loader offline-preview.test.cjs registers).
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { resolve, classify, computeArrayMove, computeArmSwitch } = require('../src/forms/schemaWalk.ts');

// The real single-file schema bundle, generated fresh into a scratch temp directory by the
// installed CLI artifact in CI, or the local CLI during development (never committed).
// This is the "study its real shape first" requirement made executable: these tests exercise
// schemaWalk against the genuine `puck.world.def.v1` shape, not a hand-typed stand-in of it.
const bundle = readSchemaBundle(__dirname);

test('atPath resolves the root and a plain nested section', () => {
  const walker = resolve(bundle);
  const root = walker.atPath([]);
  assert.equal(typeof root.schema, 'object');
  assert.ok(Array.isArray(walker.rootSections()));

  const state = walker.atPath(['state']);
  assert.equal(classify(state), 'object');
  assert.ok(state.schema.properties.world);
});

test('atPath resolves a state.world row through the bundle\'s own $defs, to the def titled WorldStateRow', () => {
  const walker = resolve(bundle);
  const row = walker.atPath(['state', 'world', 0]);
  assert.equal(walker.title(row), 'WorldStateRow');
  assert.equal(bundle['$defs'].WorldStateRow.title, 'WorldStateRow', 'the bundle def itself must carry a matching title');
});

test('a nullable $ref site (anyOf: [{$ref}, {type: "null"}]) reports nullable and resolves through to the referenced shape\'s own children', () => {
  const walker = resolve(bundle);
  // `state` itself is exactly this site — WorldDefinition.State is a nullable reference to the
  // shared WorldStateSection def (see WorldSchema.Bundle's own BuildReferenceSite).
  const rawState = bundle.properties.state;
  assert.ok(Array.isArray(rawState.anyOf) && rawState.anyOf.length === 2, 'fixture assumption: state is bundle-wrapped as anyOf: [{$ref}, {type: "null"}]');
  const state = walker.atPath(['state']);
  assert.equal(walker.isNullable(state), true);
  assert.equal(classify(state), 'object');
  assert.ok(state.schema.properties.world, 'children resolve through to the referenced WorldStateSection def, not the wrapper');
});

test('rootSections lists every root property, in schema order, with descriptions where the bundle has one', () => {
  const walker = resolve(bundle);
  const sections = walker.rootSections();
  assert.deepEqual(sections.map(s => s.key), Object.keys(bundle.properties));
  const state = sections.find(s => s.key === 'state');
  assert.ok(state);
  assert.deepEqual(state.path, ['state']);
});

test('a state.world row resolves, and its kind-conditional value/min/max/cells refine once a document supplies kind', () => {
  const walker = resolve(bundle);
  const rowPath = ['state', 'world', 0];

  // No document: the raw, unrefined shape — value stays the general anyOf. `value` itself is a
  // bundle $ref (ShapeNonNullable); atPath is what resolves it, not a bare property read.
  const bare = walker.atPath(rowPath);
  assert.equal(classify(bare), 'object');
  const bareValue = walker.atPath([...rowPath, 'value']);
  assert.ok(Array.isArray(bareValue.schema.anyOf), 'value should be the unrefined anyOf without a document');

  // With a document declaring kind: Int, value/min/max narrow to plain integers and a cell's
  // own value narrows the same way.
  const intDoc = { state: { world: [{ name: 'score', kind: 'Int', value: 0, cells: [{ key: '0', value: 1 }] }] } };
  const intRow = walker.atPath(rowPath, intDoc);
  assert.equal(intRow.schema.properties.value.type, 'integer');
  assert.equal(intRow.schema.properties.min.type, 'integer');
  const cellValue = walker.atPath([...rowPath, 'cells', 0, 'value'], intDoc);
  assert.equal(classify(cellValue), 'integer');

  // Fixed rides as an exact decimal STRING, never a lossy binary float.
  const fixedDoc = { state: { world: [{ name: 'speed', kind: 'Fixed', value: '1.5' }] } };
  const fixedRow = walker.atPath(rowPath, fixedDoc);
  assert.equal(fixedRow.schema.properties.value.type, 'string');
  assert.equal(classify(walker.atPath([...rowPath, 'value'], fixedDoc)), 'string');
});

test('a discriminated anyOf union resolves the arm a supplied document actually picked, and lists arms without one', () => {
  const walker = resolve(bundle);
  const domainPath = ['state', 'world', 0, 'domain'];

  const listed = walker.atPath(domainPath);
  assert.ok(listed.union, 'no document -> still a union node');
  assert.equal(listed.union.selected, undefined);
  assert.ok(listed.union.arms.length > 1);
  assert.equal(classify(listed), 'union');

  const document = { state: { world: [{ name: 'board', kind: 'Int', domain: { $type: 'slot' } }] } };
  const resolved = walker.atPath(domainPath, document);
  assert.equal(resolved.union.key, '$type');
  assert.equal(resolved.union.selected, 'slot');
  assert.notEqual(classify(resolved), 'union'); // an arm was picked -> classify reports the arm's own kind
});

test('defaultFor resolves a hoisted union arm\'s own $ref, not an empty stub', () => {
  const walker = resolve(bundle);
  const domainNode = walker.atPath(['state', 'world', 0, 'domain']);
  // Each arm now lives as its own bundle def (StateDomainKeysOf, StateDomainSlot, ...); the raw
  // schema references them by $ref, resolved by findDiscriminatedArms/deref before an arm ever
  // reaches `union.arms` — defaultFor has to walk through the SAME resolution. "keysOf" (its own
  // def, StateDomainKeysOf) carries a REQUIRED "row" field beside "$type" — an unresolved $ref
  // stub could produce "$type" alone (from the arm's own `const`) but never "row" too.
  const keysOfArm = domainNode.union.arms.find(arm => arm.value === 'keysOf');
  assert.ok(keysOfArm, 'fixture assumption: domain has a keysOf arm');
  const built = walker.defaultFor({ path: domainNode.path, schema: domainNode.schema, union: { ...domainNode.union, selected: 'keysOf' } });
  assert.equal(built.$type, 'keysOf');
  assert.equal(typeof built.row, 'string', `defaultFor(domain=keysOf) should resolve StateDomainKeysOf's own "row" field, got ${JSON.stringify(built)}`);
});

test('defaultFor builds a minimal object the walker can itself walk back over', () => {
  const walker = resolve(bundle);
  const rowNode = walker.atPath(['state', 'world', 0]);
  const built = walker.defaultFor(rowNode);
  assert.equal(typeof built, 'object');
  assert.equal(typeof built.name, 'string');
  assert.equal(typeof built.kind, 'string');
  assert.ok(['Int', 'Fixed', 'Bool', 'Text'].includes(built.kind));

  // Walk the produced value back through the SAME walker: children() must not throw, and the
  // kind it built now refines value/min/max, proving `built` is a genuinely walkable node.
  const document = { state: { world: [built] } };
  const rehydrated = walker.atPath(['state', 'world', 0], document);
  const entries = walker.children(rehydrated, document);
  assert.ok(entries.some(entry => entry.key === 'value'));
  const valueEntry = entries.find(entry => entry.key === 'value');
  assert.notEqual(classify(valueEntry.node), 'union');
});

test('isCellsField finds the cells array of a state row by schema location, not by an authored name', () => {
  const walker = resolve(bundle);
  const document = { state: { world: [{ name: 'anything-at-all', kind: 'Int', cells: [{ key: '0', value: 1 }] }] } };
  const cellsNode = walker.atPath(['state', 'world', 0, 'cells'], document);
  assert.equal(walker.isCellsField(cellsNode), true);
  const notCells = walker.atPath(['state', 'world', 0, 'value'], document);
  assert.equal(walker.isCellsField(notCells), false);
  const topLevelCells = walker.atPath(['motion'], document);
  assert.equal(walker.isCellsField(topLevelCells), false);
});

test('computeArrayMove swaps neighbours and is a no-op at the ends', () => {
  assert.deepEqual(computeArrayMove([1, 2, 3], 0, -1), [1, 2, 3]);
  assert.deepEqual(computeArrayMove([1, 2, 3], 2, 1), [1, 2, 3]);
  assert.deepEqual(computeArrayMove([1, 2, 3], 0, 1), [2, 1, 3]);
  assert.deepEqual(computeArrayMove(['a', 'b', 'c'], 2, -1), ['a', 'c', 'b']);
});

test('computeArmSwitch produces the chosen arm\'s own default shape, pure, without a document', () => {
  const walker = resolve(bundle);
  const domainNode = walker.atPath(['state', 'world', 0, 'domain']);
  const switched = computeArmSwitch(walker, domainNode, 'keysOf');
  assert.equal(switched.$type, 'keysOf');
  assert.equal(typeof switched.row, 'string');
  // "ordered" is optional (its own default lives on the schema, not in `required`), so a
  // MINIMAL value omits it entirely rather than materializing every optional field.
  assert.equal('ordered' in switched, false);
});
