const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { readDocumentRole, hasComposition, ISLAND_ROOT_DOCUMENT_NAME } = require('../src/document/documentRole.ts');

function repositoryRoot() {
  let dir = __dirname;
  while (true) {
    if (fs.existsSync(path.join(dir, 'Puck.slnx'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error('could not find Puck.slnx walking up from ' + __dirname);
    dir = parent;
  }
}

const root = repositoryRoot();
const worldsDir = path.join(root, 'src', 'Puck.World', 'Assets', 'worlds');

test('ISLAND_ROOT_DOCUMENT_NAME matches the real official root file name', () => {
  assert.equal(ISLAND_ROOT_DOCUMENT_NAME, 'puck.world.json');
  assert.ok(fs.existsSync(path.join(worldsDir, ISLAND_ROOT_DOCUMENT_NAME)));
});

test('readDocumentRole reads the real island root as world', () => {
  const value = JSON.parse(fs.readFileSync(path.join(worldsDir, 'puck.world.json'), 'utf8'));
  assert.equal(readDocumentRole(value), 'world');
  assert.equal(hasComposition(value), true, 'the island root itself composes over standard.basis.json');
});

test('readDocumentRole reads a real fragment (non-empty exports) as fragment', () => {
  const value = JSON.parse(fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8'));
  assert.equal(readDocumentRole(value), 'fragment');
});

test('readDocumentRole reads standard.basis.json (no documentId, no exports) as basis', () => {
  const value = JSON.parse(fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8'));
  assert.equal(readDocumentRole(value), 'basis');
  assert.equal(hasComposition(value), false, 'the basis document itself composes over nothing');
});

test('readDocumentRole reads a real shard as world — a stated content-only limitation, not a bug: ' +
  'a shard carries a documentId exactly like the island root and nothing in its own content says otherwise', () => {
  const value = JSON.parse(fs.readFileSync(path.join(worldsDir, 'shards', 'quilt-ne.world.json'), 'utf8'));
  assert.equal(value.documentId, 'quilt-ne');
  assert.equal(readDocumentRole(value), 'world');
  assert.equal(hasComposition(value), true, 'a shard composes over ../puck.world.json');
});

test('readDocumentRole prefers fragment over world when both documentId and exports are present', () => {
  assert.equal(readDocumentRole({ documentId: 'x', exports: { reads: ['a'] } }), 'fragment');
});

test('readDocumentRole falls back to basis for a document with neither documentId nor exports', () => {
  assert.equal(readDocumentRole({}), 'basis');
  assert.equal(readDocumentRole(null), 'basis');
  assert.equal(readDocumentRole('not an object'), 'basis');
});

test('readDocumentRole treats an empty exports object, and exports whose lists are empty or all-empty-strings, as absent', () => {
  assert.equal(readDocumentRole({ documentId: 'd', exports: {} }), 'world');
  assert.equal(readDocumentRole({ documentId: 'd', exports: { reads: [], actions: null, bindings: [''] } }), 'world');
});

test('hasComposition is true for a non-empty imports list even without a basis', () => {
  assert.equal(hasComposition({ imports: [{ document: 'x.json' }] }), true);
  assert.equal(hasComposition({ imports: [] }), false);
  assert.equal(hasComposition({}), false);
});
