// The audit trail's journal stream: bounded concurrent downloads, one newest-first timeline, and batch failures
// that drop out rather than fail the trail.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { lastValueFrom } = require('rxjs');
const { auditJournal, JOURNAL_FETCH_CONCURRENCY, JOURNAL_FETCH_LIMIT } = require('../src/components/auditJournal.ts');

const batch = (time, name) => ({ value: [{ data: { url: `https://account.example/user/private/${name}` }, time, type: 'Microsoft.Storage.BlobCreated' }] });
const sources = (names, overrides = {}) => ({
  headers: async () => ({ Authorization: 'Bearer token' }),
  listJournalNames: async () => names,
  resolveEndpoint: async () => 'https://account.example',
  userObjectId: 'user',
  ...overrides,
});

test('downloads stay within the concurrency bound and only the newest batches are read', async () => {
  const names = Array.from({ length: JOURNAL_FETCH_LIMIT + 15 }, (_, i) => `system/events/blob/${String(i).padStart(4, '0')}.json`);
  let inFlight = 0, peak = 0;
  const downloaded = [];
  const state = await lastValueFrom(auditJournal(sources(names), async (url) => {
    inFlight++;
    peak = Math.max(peak, inFlight);
    downloaded.push(url);
    await new Promise((resolve) => setTimeout(resolve, 2));
    inFlight--;
    return batch('2026-09-01T00:00:00Z', url.split('/').pop());
  }));
  assert.equal(peak, JOURNAL_FETCH_CONCURRENCY);
  assert.equal(downloaded.length, JOURNAL_FETCH_LIMIT);
  assert.ok(!downloaded.some((url) => url.endsWith('/0000.json')), 'the oldest batches fall outside the limit');
  assert.equal(state.journalUrls[0], 'https://account.example/user/system/events/blob/0074.json');
});

test('a failed batch drops out and the rest merge newest first', async () => {
  const names = ['system/events/blob/a.json', 'system/events/blob/b.json', 'system/events/blob/c.json'];
  const state = await lastValueFrom(auditJournal(sources(names), async (url) => {
    if (url.endsWith('/b.json')) throw new Error('HTTP 404');
    return url.endsWith('/a.json') ? batch('2026-09-01T00:00:00Z', 'old.csv') : batch('2026-09-02T00:00:00Z', 'new.csv');
  }));
  assert.equal(state.error, undefined);
  assert.deepEqual(state.events.map((event) => event.blobPath), ['private/new.csv', 'private/old.csv']);
});

test('an unreachable account is the trail\'s error', async () => {
  const state = await lastValueFrom(auditJournal(sources([], { resolveEndpoint: () => Promise.reject(new Error('no account')) }), async () => ({})));
  assert.equal(state.error, 'no account');
  assert.equal(state.events, undefined);
});
