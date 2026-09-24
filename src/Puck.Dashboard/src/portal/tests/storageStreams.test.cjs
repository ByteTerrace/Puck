// The Cloud Storage page's timing rules, driven with promises the test resolves by hand: a newer query or listing
// always wins over an older one still in flight, and every reload settles, even one a newer reload superseded.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { Subject } = require('rxjs');
const { storageAccountState, storageQueryRuns } = require('../src/components/data/storageStreams.ts');

function deferred() {
  let resolve, reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, reject, resolve };
}
const settle = () => new Promise((resolve) => setImmediate(resolve));

test('a newer query run supersedes an older one still in flight; its result never lands', async () => {
  const runs = new Subject(), pending = new Map();
  const states = [];
  const subscription = storageQueryRuns(runs, (sql) => { const d = deferred(); pending.set(sql, d); return d.promise; })
    .subscribe((state) => states.push(state));
  runs.next('slow');
  runs.next('fast');
  pending.get('fast').resolve({ columns: ['a'], rows: [['fast']] });
  await settle();
  pending.get('slow').resolve({ columns: ['a'], rows: [['slow']] });
  await settle();
  const last = states.at(-1);
  assert.equal(last.isRunning, false);
  assert.deepEqual(last.output.rows, [['fast']]);
  assert.ok(!states.some((state) => state.output?.rows?.[0]?.[0] === 'slow'), 'the superseded result must never be emitted');
  subscription.unsubscribe();
});

test('a failing query reports its reason and stops running', async () => {
  const runs = new Subject();
  const states = [];
  const subscription = storageQueryRuns(runs, () => Promise.reject(new Error('No files found')))
    .subscribe((state) => states.push(state));
  runs.next('SELECT 1');
  await settle();
  assert.deepEqual(states.at(-1), { isRunning: false, output: undefined, queryError: 'No files found' });
  subscription.unsubscribe();
});

function account(overrides = {}) {
  const privateLoads = [], publicLoads = [];
  const sources = {
    listPrivate: () => { const d = deferred(); privateLoads.push(d); return d.promise; },
    listPublic: () => { const d = deferred(); publicLoads.push(d); return d.promise; },
    resolveEndpoint: async () => 'https://account.example',
    ...overrides,
  };
  const reloads = new Subject(), states = [];
  const subscription = storageAccountState(sources, reloads).subscribe((state) => states.push(state));
  return { privateLoads, publicLoads, reloads, states, subscription };
}

test('the account loads both listings once its endpoint resolves', async () => {
  const { privateLoads, publicLoads, states, subscription } = account();
  await settle();
  privateLoads[0].resolve([{ name: 'a.parquet' }]);
  publicLoads[0].resolve([{ name: 'b.csv' }]);
  await settle();
  const last = states.at(-1);
  assert.equal(last.storageEndpoint, 'https://account.example');
  assert.deepEqual(last.blobs, [{ name: 'a.parquet' }]);
  assert.deepEqual(last.publicFiles, [{ name: 'b.csv' }]);
  subscription.unsubscribe();
});

test('a reload settles once its listing lands, and a superseded reload settles without landing', async () => {
  const { privateLoads, reloads, states, subscription } = account();
  await settle();
  privateLoads[0].resolve([]);
  await settle();
  let firstSettled = false, secondSettled = false;
  reloads.next({ includePublic: false, settled: () => { firstSettled = true; } });
  reloads.next({ includePublic: false, settled: () => { secondSettled = true; } });
  await settle();
  assert.equal(firstSettled, true, 'the superseded reload must still settle its caller');
  assert.equal(secondSettled, false);
  privateLoads[2].resolve([{ name: 'new' }]);
  await settle();
  assert.equal(secondSettled, true);
  privateLoads[1].resolve([{ name: 'stale' }]);
  await settle();
  assert.deepEqual(states.at(-1).blobs, [{ name: 'new' }]);
  subscription.unsubscribe();
});

test('an unresolvable endpoint and a failed published listing both surface as the list error', async () => {
  const unreachable = account({ resolveEndpoint: () => Promise.reject(new Error('no account')) });
  await settle();
  assert.equal(unreachable.states.at(-1).listError, 'no account');
  unreachable.subscription.unsubscribe();
  const { privateLoads, publicLoads, states, subscription } = account();
  await settle();
  privateLoads[0].resolve([]);
  publicLoads[0].reject(new Error('published listing failed'));
  await settle();
  assert.equal(states.at(-1).listError, 'published listing failed');
  subscription.unsubscribe();
});

test('a reload after an unresolvable endpoint asks again, and lists once it resolves', async () => {
  let attempts = 0;
  const { privateLoads, reloads, states, subscription } = account({
    resolveEndpoint: async () => {
      attempts += 1;
      if (1 === attempts) throw new Error('no account yet');
      return 'https://account.example';
    },
  });
  await settle();
  assert.equal(states.at(-1).listError, 'no account yet');
  let settled = false;
  reloads.next({ includePublic: false, settled: () => { settled = true; } });
  await settle();
  assert.equal(attempts, 2, 'the failed resolution is not remembered');
  assert.equal(states.at(-1).listError, undefined, 'the retry clears the old error');
  assert.equal(states.at(-1).storageEndpoint, 'https://account.example');
  privateLoads[0].resolve([{ name: 'a.parquet' }]);
  await settle();
  assert.equal(settled, true);
  assert.deepEqual(states.at(-1).blobs, [{ name: 'a.parquet' }]);
  reloads.next({ includePublic: false, settled: () => {} });
  await settle();
  assert.equal(attempts, 2, 'a resolved endpoint is kept across reloads');
  subscription.unsubscribe();
});

test('a superseded query run is cancelled in the engine, and so is one still running when the page lets go', async () => {
  const runs = new Subject();
  const signals = [];
  const subscription = storageQueryRuns(runs, (sqlText, signal) => {
    signals.push({ sqlText, signal });
    return new Promise(() => {});
  }).subscribe(() => {});
  runs.next('SELECT 1');
  runs.next('SELECT 2');
  assert.equal(signals[0].signal.aborted, true, 'the first run was abandoned, so its query is cancelled');
  assert.equal(signals[1].signal.aborted, false);
  subscription.unsubscribe();
  assert.equal(signals[1].signal.aborted, true, 'leaving the page cancels the run in flight');
});
