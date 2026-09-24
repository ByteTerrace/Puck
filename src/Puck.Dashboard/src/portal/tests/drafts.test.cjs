const assert = require('node:assert/strict');
const { test } = require('node:test');

require('./support/register.cjs');

const { LocalDraftStore, DraftRefusal, MAX_REVISIONS_PER_DRAFT, readDraftListing } = require('../src/document/localDrafts.ts');
const { MAX_DOCUMENT_BYTES } = require('../src/document/intake.ts');

const DRAFTS_KEY = 'byteterrace.puck.studioSourceDrafts';

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

test('save creates a draft with one revision of changed files, and list/load see it', () => {
  const store = new LocalDraftStore(memoryStorage());
  const draft = store.save('my-draft', 'My Draft', 'games/klondike', { 'games/klondike.puck': 'rule "a" {}' }, '1 changed file');

  assert.equal(draft.id, 'my-draft');
  assert.equal(draft.title, 'My Draft');
  assert.equal(draft.documentName, 'games/klondike');
  assert.equal(draft.revisions.length, 1);
  assert.deepEqual(draft.revisions[0].files, { 'games/klondike.puck': 'rule "a" {}' });
  assert.equal(draft.revisions[0].label, '1 changed file');
  assert.ok(draft.revisions[0].savedAt.length > 0);

  assert.deepEqual(store.list().map((d) => d.id), ['my-draft']);
  assert.deepEqual(store.load('my-draft'), draft);
});

test('repeated saves prepend revisions, newest first, capped at ten', () => {
  const store = new LocalDraftStore(memoryStorage());
  for (let i = 0; i < MAX_REVISIONS_PER_DRAFT + 5; i += 1) {
    store.save('my-draft', 'My Draft', 'counter', { 'counter.puck': `// ${i}` }, `edit ${i}`);
  }
  const draft = store.load('my-draft');
  assert.equal(draft.revisions.length, MAX_REVISIONS_PER_DRAFT);
  assert.equal(draft.revisions[0].files['counter.puck'], '// 14');
  assert.equal(draft.revisions[MAX_REVISIONS_PER_DRAFT - 1].files['counter.puck'], '// 5');
});

test('a later save keeps an existing title when a blank one is supplied', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('my-draft', 'First Title', 'counter', {}, 'first');
  const draft = store.save('my-draft', '', 'counter', {}, 'second');
  assert.equal(draft.title, 'First Title');
});

test('a draft over the 2 MB cap is refused by name and nothing is written', () => {
  const backing = memoryStorage();
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.save('big', 'Big', 'counter', { 'counter.puck': 'x'.repeat(MAX_DOCUMENT_BYTES) }, 'big'), /byte cap/);
  assert.equal(backing.entries.size, 0);
});

test('delete removes a draft and reports whether one existed', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('my-draft', 'My Draft', 'counter', {}, 'opened');
  assert.equal(store.delete('my-draft'), true);
  assert.equal(store.load('my-draft'), undefined);
  assert.equal(store.delete('my-draft'), false);
});

test('an invalid id is refused by name, for both save and delete, without touching storage', () => {
  const backing = memoryStorage();
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.save('Not Valid!', 't', 'n', {}, 'l'), DraftRefusal);
  assert.throws(() => store.delete('constructor'), DraftRefusal);
  assert.equal(backing.entries.size, 0);
});

test('unreadable storage refuses by name rather than silently starting empty, and the listing reports it', () => {
  for (const raw of ['not json', '{"broken":{"id":"broken"}}', '{"a":{"id":"a","title":"A","documentName":"n","revisions":[{"files":{"x":1},"label":"l","savedAt":"s"}]}}']) {
    const backing = memoryStorage();
    backing.setItem(DRAFTS_KEY, raw);
    const store = new LocalDraftStore(backing);
    assert.throws(() => store.list(), DraftRefusal);
    assert.throws(() => store.save('my-draft', 't', 'n', {}, 'l'), DraftRefusal);
    assert.match(readDraftListing(store).error, /unreadable/);
    assert.equal(backing.getItem(DRAFTS_KEY), raw, 'the unreadable library is left as it was');
  }
});

test('a storage write failure (quota exceeded) refuses by name and never throws a raw error', () => {
  const backing = {
    getItem: () => null,
    setItem: () => { throw new Error('QuotaExceededError'); },
  };
  const store = new LocalDraftStore(backing);
  assert.throws(() => store.save('my-draft', 't', 'n', {}, 'l'), DraftRefusal);
});

test('multiple drafts are independent', () => {
  const store = new LocalDraftStore(memoryStorage());
  store.save('draft-a', 'A', 'counter', { 'counter.puck': 'a' }, 'a1');
  store.save('draft-b', 'B', 'games/token', { 'games/token.puck': 'b' }, 'b1');
  assert.deepEqual(store.list().map((d) => d.id).sort(), ['draft-a', 'draft-b']);
  assert.equal(store.load('draft-a').documentName, 'counter');
  assert.equal(store.load('draft-b').documentName, 'games/token');
});
