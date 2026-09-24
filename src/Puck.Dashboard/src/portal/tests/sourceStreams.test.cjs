// The source edit loop's streams, in Node with no browser and no engine: the language server pump, the diagnostics
// the machine reads, the island check, and the editor's transport. Every assertion counts engine calls — lsp messages,
// idle units, compositions — and none reads a clock.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { Subject, VirtualTimeScheduler } = require('rxjs');
const { pumpLanguageServer, openLanguageChannel } = require('../src/native/languagePump.ts');
const { channelTransport } = require('../src/native/lspTransport.ts');
const { sourceDiagnostics } = require('../src/machines/studio/sourceDiagnostics.ts');
const { islandChecks } = require('../src/machines/studio/islandCheck.ts');
const { sourceUri } = require('../src/document/sourcePaths.ts');

const change = (path, version, text) => JSON.stringify({
  jsonrpc: '2.0', method: 'textDocument/didChange',
  params: { textDocument: { uri: sourceUri(path), version }, contentChanges: [{ text }] },
});
const completion = (id, path) => JSON.stringify({
  jsonrpc: '2.0', id, method: 'textDocument/completion', params: { textDocument: { uri: sourceUri(path) }, position: { line: 0, character: 0 } },
});

/** A language server stand-in: a change marks its URI dirty, and each idle unit diagnoses one dirty URI. */
function fakeServer() {
  const log = [];
  const dirty = [];
  const counts = { lsp: 0, idle: 0, units: 0 };
  return {
    log, counts,
    lsp(text) {
      counts.lsp++;
      const message = JSON.parse(text);
      log.push(`lsp ${message.method}${message.params?.contentChanges ? ` ${message.params.contentChanges[0].text}` : ''}`);
      if (message.method === 'textDocument/didChange') {
        const uri = message.params.textDocument.uri;
        if (!dirty.includes(uri)) dirty.push(uri);
        return [];
      }
      return message.id === undefined ? [] : [{ jsonrpc: '2.0', id: message.id, result: { idleUnitsBefore: counts.units } }];
    },
    lspIdle() {
      counts.idle++;
      const uri = dirty.shift();
      if (!uri) return { ran: false, pending: false, messages: [] };
      counts.units++;
      log.push(`idle ${uri}`);
      return {
        ran: true,
        pending: dirty.length > 0,
        messages: [{ jsonrpc: '2.0', method: 'textDocument/publishDiagnostics', params: { uri, version: null, diagnostics: [] } }],
      };
    },
  };
}

/** A scheduler that runs one scheduled step at a time, so a test can interleave a message between two steps. */
function steppingScheduler() {
  const queue = [];
  return {
    now: () => 0,
    schedule(work) {
      const entry = { work, cancelled: false };
      queue.push(entry);
      return { unsubscribe: () => { entry.cancelled = true; } };
    },
    step() {
      const entry = queue.shift();
      if (entry && !entry.cancelled) entry.work();
      return entry !== undefined;
    },
    get pending() { return queue.filter((entry) => !entry.cancelled).length; },
  };
}

test('a burst of edits to one file costs one server call and one diagnostic unit', () => {
  const server = fakeServer();
  const scheduler = new VirtualTimeScheduler();
  const inbound = new Subject();
  const out = [];
  pumpLanguageServer(inbound, server, scheduler).subscribe((message) => out.push(message));
  for (let version = 1; version <= 25; version++) inbound.next(change('counter.puck', version, `text ${version}`));
  scheduler.flush();

  assert.equal(server.counts.lsp, 1, 'the queued full-text changes coalesce to the newest');
  assert.equal(server.log[0], 'lsp textDocument/didChange text 25');
  assert.equal(server.counts.units, 1, 'one unit per dirty file');
  assert.equal(server.counts.idle, 1, 'the unit said nothing more was pending, so the idling stopped');
  assert.equal(out.length, 1);
});

test('edits to two files diagnose each file once, and changes to different files never coalesce', () => {
  const server = fakeServer();
  const scheduler = new VirtualTimeScheduler();
  const inbound = new Subject();
  pumpLanguageServer(inbound, server, scheduler).subscribe();
  inbound.next(change('a.puck', 1, 'a1'));
  inbound.next(change('b.puck', 1, 'b1'));
  inbound.next(change('b.puck', 2, 'b2'));
  inbound.next(change('a.puck', 2, 'a2'));
  scheduler.flush();
  assert.equal(server.counts.lsp, 3, 'only the consecutive pair for b.puck coalesced');
  assert.equal(server.counts.units, 2);
  assert.equal(server.counts.idle, 2);
});

test('a request queued behind an edit is answered before any diagnostic unit runs', () => {
  const server = fakeServer();
  const scheduler = new VirtualTimeScheduler();
  const inbound = new Subject();
  const out = [];
  pumpLanguageServer(inbound, server, scheduler).subscribe((message) => out.push(message));
  inbound.next(change('counter.puck', 1, 'edited'));
  inbound.next(completion(7, 'counter.puck'));
  scheduler.flush();
  const answer = out.find((message) => message.id === 7);
  assert.equal(answer.result.idleUnitsBefore, 0, 'zero idle units ran before the completion');
  assert.equal(server.counts.units, 1);
});

test('a request that arrives while diagnostics are pending is taken at the next step, before the next unit', () => {
  const server = fakeServer();
  const scheduler = steppingScheduler();
  const inbound = new Subject();
  const out = [];
  pumpLanguageServer(inbound, server, scheduler).subscribe((message) => out.push(message));
  inbound.next(change('a.puck', 1, 'a'));
  inbound.next(change('b.puck', 1, 'b'));
  scheduler.step();
  scheduler.step();
  scheduler.step();
  assert.equal(server.counts.units, 1, 'one unit ran, one is still pending');

  inbound.next(completion(9, 'a.puck'));
  scheduler.step();
  assert.equal(out.at(-1).id, 9, 'the request went first');
  assert.equal(server.counts.units, 1);
  while (scheduler.step());
  assert.equal(server.counts.units, 2);
  assert.equal(scheduler.pending, 0, 'the pump stops once nothing is pending');
});

test('a running unit is atomic: a message that arrives during it waits for it to finish', async () => {
  const server = fakeServer();
  let finish;
  const idle = server.lspIdle.bind(server);
  server.lspIdle = () => new Promise((resolve) => { finish = () => resolve(idle()); });
  const scheduler = steppingScheduler();
  const inbound = new Subject();
  pumpLanguageServer(inbound, server, scheduler).subscribe();
  inbound.next(change('a.puck', 1, 'a'));
  scheduler.step();
  scheduler.step();
  assert.ok(finish, 'the unit is running');
  inbound.next(completion(3, 'a.puck'));
  assert.equal(scheduler.pending, 0, 'nothing else is scheduled while the unit runs');
  finish();
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(scheduler.pending, 1);
  scheduler.step();
  assert.equal(server.log.at(-1), 'lsp textDocument/completion');
});

test('an open channel shares the pump\'s messages, and the transport hands lsp-client JSON text', () => {
  const server = fakeServer();
  const scheduler = new VirtualTimeScheduler();
  const channel = openLanguageChannel(server, scheduler);
  const transport = channelTransport(channel);
  const received = [];
  const handler = (text) => received.push(JSON.parse(text));
  transport.subscribe(handler);
  transport.subscribe(handler);
  transport.send(completion(1, 'counter.puck'));
  scheduler.flush();
  assert.equal(received.length, 1, 'a handler subscribed twice receives each message once');
  assert.equal(received[0].id, 1);
  transport.unsubscribe(handler);
  transport.send(completion(2, 'counter.puck'));
  scheduler.flush();
  assert.equal(received.length, 1);
  channel.close();
});

test('diagnostics keep the newest publication per file and drop an older version', () => {
  const messages = new Subject();
  const seen = [];
  sourceDiagnostics(messages).subscribe((published) => seen.push(published));
  const publish = (path, version, message) => messages.next({
    jsonrpc: '2.0', method: 'textDocument/publishDiagnostics',
    params: { uri: sourceUri(path), version, diagnostics: message ? [{ range: { start: { line: 2, character: 4 }, end: { line: 2, character: 9 } }, severity: 1, code: 'PUCK040', message }] : [] },
  });
  publish('counter.puck', 2, 'newer');
  publish('counter.puck', 1, 'older');
  publish('other.puck', 1, null);
  messages.next({ jsonrpc: '2.0', id: 4, result: null });
  assert.deepEqual(seen.map((published) => [published.path, published.version]), [['counter.puck', 2], ['other.puck', 1]]);
  assert.deepEqual(seen[0].diagnostics[0], { code: 'PUCK040', severity: 'error', message: 'newer', path: 'counter.puck', line: 3, column: 5, length: 5 });
});

test('the island check runs one composition at a time, keeps only the newest waiting revision, and drops stale results', async () => {
  const requests = new Subject();
  const runs = [];
  const results = [];
  const job = (revision) => () => new Promise((resolve) => runs.push({ revision, finish: () => resolve(`composed ${revision}`) }));
  islandChecks(requests).subscribe((result) => results.push(result));

  requests.next({ revision: 0, run: job(0) });
  requests.next({ revision: 0, run: job(0) });
  requests.next({ revision: 1, run: job(1) });
  requests.next({ revision: 2, run: job(2) });
  assert.deepEqual(runs.map((run) => run.revision), [0], 'one in flight; the same revision twice runs once');

  runs[0].finish();
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(results, [], 'revision 0 finished after revision 2 arrived, so it is dropped');
  assert.deepEqual(runs.map((run) => run.revision), [0, 2], 'revision 1 was replaced while it waited');

  requests.next({ revision: 3, run: null });
  runs[1].finish();
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(results, [], 'a newer revision that is not ready yet still makes revision 2 stale');

  requests.next({ revision: 3, run: job(3) });
  runs[2].finish();
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(results, [{ revision: 3, outcome: 'composed 3' }]);
  assert.equal(runs.length, 3);
});
