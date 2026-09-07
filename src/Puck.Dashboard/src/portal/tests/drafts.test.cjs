const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const { LocalDraftStore, DraftRefusal, MAX_REVISIONS_PER_DRAFT } = require('../src/document/localDrafts.ts');

function memoryStorage() {
  const entries = new Map();
  return {
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => entries.set(key, value),
    entries,
  };
}

test('a fresh store lists nothing and loads nothing', () => {
  const store = new LocalDraftStore(memoryStorage());
  assert.deepEqual(store.list(), []);
  assert.equal(store.load('anything'), undefined);
});

test('save creates a draft with one revision, and list/load see it', () => {
  const store = new LocalDraftStore(memoryStorage());
  const draft = store.save('my-draft', 'My Draft', 'puck.world.json', '{"a":1}', 'Opened document');

  assert.equal(draft.id, 'my-draft');
  assert.equal(draft.title, 'My Draft');
  assert.equal(draft.documentName, 'puck.world.json');
  assert.equal(draft.revisions.length, 1);
  assert.equal(draft.revisions[0].text, '{"a":1}');
  assert.equal(draft.revisions[0].label, 'Opened document');
  assert.ok(draft.revisions[0].savedAt.length > 0);

  assert.deepEqual(store.list().map((d) => d.id), ['my-draft']);
  assert.deepEqual(store.load('my-draft'), draft);
});

test('repeated saves prepend revisions, newest first, capped at ten', () => {
  const store = new LocalDraftStore(memoryStorage());
  for (let i = 0; i < MAX_REVISIONS_PER_DRAFT + 5; i += 1) {
    store.save('my-draft', 'My Draft', 'puck.world.json', `{"i":${i}}`, `edit ${i}`);
  }
  const draft = store.load('my-draft');
  assert.equal(draft.revisions.length, MAX_REVISIONS_PER_DRAFT);
  // Newest first: the very last save (i = 14) leads, and the ten kept are the ten most recent.
  assert.equal(draft.revisions[0].text, '{"i":14}');
  assert.equal(draft.revisions[MAX_REVISIONS_PER_DRAFT - 1].text, '{"i":5}');
});

test('a later save keeps an existing title when a blank one is supplied', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('my-draft', 'First Title', 'puck.world.json', '{}', 'first');
  const draft = store.save('my-draft', '', 'puck.world.json', '{}', 'second');
  assert.equal(draft.title, 'First Title');
});

test('delete removes a draft and reports whether one existed', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('my-draft', 'My Draft', 'puck.world.json', '{}', 'opened');
  assert.equal(store.delete('my-draft'), true);
  assert.equal(store.load('my-draft'), undefined);
  assert.equal(store.delete('my-draft'), false);
});

test('an invalid id is refused by name, for both save and delete, without touching storage', () => {
  const backing = memoryStorage();
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.save('Not Valid!', 't', 'n', '{}', 'l'), DraftRefusal);
  assert.throws(() => store.delete('constructor'), DraftRefusal);
  assert.equal(backing.entries.size, 0);
});

test('unreadable storage (corrupt JSON) refuses by name rather than silently starting empty', () => {
  const backing = memoryStorage();
  backing.setItem('byteterrace.puck.studioDrafts.v1', 'not json');
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.list(), DraftRefusal);
  assert.throws(() => store.save('my-draft', 't', 'n', '{}', 'l'), DraftRefusal);
});

test('a storage write failure (quota exceeded) refuses by name and never throws a raw error', () => {
  const backing = {
    getItem: () => null,
    setItem: () => { throw new Error('QuotaExceededError'); },
  };
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.save('my-draft', 't', 'n', '{}', 'l'), DraftRefusal);
});

test('multiple drafts are independent', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('draft-a', 'A', 'puck.world.json', '{"a":1}', 'a1');
  store.save('draft-b', 'B', 'games/tictactoe.world.json', '{"b":1}', 'b1');
  assert.deepEqual(store.list().map((d) => d.id).sort(), ['draft-a', 'draft-b']);
  assert.equal(store.load('draft-a').documentName, 'puck.world.json');
  assert.equal(store.load('draft-b').documentName, 'games/tictactoe.world.json');
});
