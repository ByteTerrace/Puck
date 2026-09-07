const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { getAt, setAt, deleteAt, pathToPointer, pointerToPath, pathToEnginePath } = require('../src/document/jsonPath.ts');

test('getAt reads nested object and array segments, and reads through absent segments as undefined', () => {
  const doc = { state: { world: [{ name: 'score', cells: [{ key: '0', value: 7 }] }] } };
  assert.equal(getAt(doc, ['state', 'world', 0, 'name']), 'score');
  assert.equal(getAt(doc, ['state', 'world', 0, 'cells', 0, 'value']), 7);
  assert.equal(getAt(doc, []), doc);
  assert.equal(getAt(doc, ['state', 'world', 5, 'name']), undefined);
  assert.equal(getAt(doc, ['missing', 'deeper']), undefined);
  assert.equal(getAt(null, ['a']), undefined);
});

test('setAt is immutable: it shares every untouched subtree with the original root', () => {
  const doc = { state: { world: [{ name: 'a' }, { name: 'b' }], other: { untouched: true } } };
  const next = setAt(doc, ['state', 'world', 0, 'name'], 'renamed');

  assert.equal(next.state.world[0].name, 'renamed');
  assert.equal(doc.state.world[0].name, 'a', 'the original root is never mutated');

  // Everything not on the edited path is the SAME object reference, not merely equal.
  assert.strictEqual(next.state.other, doc.state.other);
  assert.strictEqual(next.state.world[1], doc.state.world[1]);
  assert.notStrictEqual(next.state.world, doc.state.world);
  assert.notStrictEqual(next.state.world[0], doc.state.world[0]);
});

test('setAt creates missing intermediate containers, shaped by the segment that indexes into them', () => {
  const built = setAt(undefined, ['state', 'world', 2, 'name'], 'c');
  assert.equal(built.state.world.length, 3);
  assert.equal(built.state.world[0], null, 'a skipped array index pads with null, a valid JSON value');
  assert.equal(built.state.world[1], null);
  assert.equal(built.state.world[2].name, 'c');

  // Replacing the whole document (the empty path) just returns the new value outright.
  assert.deepEqual(setAt({ old: true }, [], { fresh: true }), { fresh: true });
});

test('deleteAt removes an object key entirely and splices an array index, sharing untouched subtrees, and is a no-op when nothing is there', () => {
  const doc = { state: { world: [{ name: 'a' }, { name: 'b' }, { name: 'c' }] }, keep: {} };
  const removedKey = deleteAt(doc, ['state', 'world', 1, 'name']);
  assert.equal('name' in removedKey.state.world[1], false);
  assert.strictEqual(removedKey.keep, doc.keep);
  assert.strictEqual(removedKey.state.world[0], doc.state.world[0]);

  const spliced = deleteAt(doc, ['state', 'world', 1]);
  assert.deepEqual(spliced.state.world.map(r => r.name), ['a', 'c']);
  assert.strictEqual(spliced.state.world[0], doc.state.world[0]);
  assert.strictEqual(spliced.state.world[1], doc.state.world[2]);

  const untouched = deleteAt(doc, ['state', 'world', 99, 'name']);
  assert.strictEqual(untouched, doc);
  const untouchedRoot = deleteAt(doc, []);
  assert.strictEqual(untouchedRoot, doc);
});

test('pathToPointer / pointerToPath round-trip RFC 6901 pointers, escaping ~ and /', () => {
  const path = ['state', 'world', 3, 'cells', 0, 'key'];
  const pointer = pathToPointer(path);
  assert.equal(pointer, '/state/world/3/cells/0/key');
  assert.deepEqual(pointerToPath(pointer), path);
  assert.equal(pathToPointer([]), '');
  assert.deepEqual(pointerToPath(''), []);

  const weird = ['a/b', 'c~d', 0];
  assert.equal(pathToPointer(weird), '/a~1b/c~0d/0');
  assert.deepEqual(pointerToPath(pathToPointer(weird)), weird);
});

test('pathToEnginePath spells the same address the engine\'s own Parse errors use', () => {
  assert.equal(pathToEnginePath(['state', 'world', 3, 'cells', 0, 'key']), 'state.world[3].cells[0].key');
  assert.equal(pathToEnginePath([]), '');
  assert.equal(pathToEnginePath(['schema']), 'schema');
  assert.equal(pathToEnginePath([0, 1]), '[0][1]');
});
