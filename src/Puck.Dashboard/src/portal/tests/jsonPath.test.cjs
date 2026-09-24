const assert = require('node:assert/strict');
const { test } = require('node:test');

require('./support/register.cjs');

const { getAt, pathToPointer, pointerToPath } = require('../src/document/jsonPath.ts');

test('getAt reads nested object and array segments, and reads through absent segments as undefined', () => {
  const doc = { state: { world: [{ name: 'score', cells: [{ key: '0', value: 7 }] }] } };
  assert.equal(getAt(doc, ['state', 'world', 0, 'name']), 'score');
  assert.equal(getAt(doc, ['state', 'world', 0, 'cells', 0, 'value']), 7);
  assert.equal(getAt(doc, []), doc);
  assert.equal(getAt(doc, ['state', 'world', 5, 'name']), undefined);
  assert.equal(getAt(doc, ['missing', 'deeper']), undefined);
  assert.equal(getAt(null, ['a']), undefined);
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

