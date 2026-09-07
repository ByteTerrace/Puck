const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText, file);
const { connectWorkerEngine } = require('../src/native/workerClient.ts');

class WorkerHarness extends EventTarget {
  messages = [];
  terminated = 0;
  postMessage(message) { this.messages.push(message); }
  terminate() { this.terminated++; }
  reply(data) { this.dispatchEvent(new MessageEvent('message', { data })); }
}
const bootRequest = { kind: 'init', engineEntryUrl: 'test://engine' };
async function ready(worker) {
  const boot = connectWorkerEngine(worker, bootRequest);
  worker.reply({ kind: 'ready' });
  return boot;
}

test('worker transport correlates out-of-order replies and preserves bigint', async () => {
  const worker = new WorkerHarness(), engine = await ready(worker);
  const first = engine.readRow('h', 'counter'), second = engine.stateHash('h');
  const [, a, b] = worker.messages;
  worker.reply({ kind: 'result', id: b.id, ok: true, value: 'hash' });
  worker.reply({ kind: 'result', id: a.id, ok: true, value: 9223372036854775807n });
  assert.equal(await first, 9223372036854775807n);
  assert.equal(await second, 'hash');
  await engine.dispose();
});

test('disposal rejects pending and subsequent calls and terminates exactly once', async () => {
  const worker = new WorkerHarness(), engine = await ready(worker);
  const pending = assert.rejects(engine.compile('{}'), /disposed/);
  await engine.dispose();
  await pending;
  await assert.rejects(engine.version(), /disposed/);
  await engine.dispose();
  assert.equal(worker.terminated, 1);
});

test('worker load errors and undecodable messages reject boot or pending calls', async () => {
  for (const kind of ['error', 'messageerror']) {
    const loading = new WorkerHarness();
    const boot = assert.rejects(connectWorkerEngine(loading, bootRequest), /failed|decoded/);
    loading.dispatchEvent(new Event(kind));
    await boot;
    assert.equal(loading.terminated, 1);
    const running = new WorkerHarness(), engine = await ready(running);
    const pending = assert.rejects(engine.rows('h'), /failed|decoded/);
    running.dispatchEvent(new Event(kind));
    await pending;
    assert.equal(running.terminated, 1);
  }
});

test('aborting boot terminates immediately, including an already-aborted signal', async () => {
  for (const alreadyAborted of [false, true]) {
    const worker = new WorkerHarness(), controller = new AbortController();
    if (alreadyAborted) controller.abort();
    const boot = assert.rejects(connectWorkerEngine(worker, bootRequest, controller.signal), /cancelled/);
    controller.abort();
    await boot;
    assert.equal(worker.terminated, 1);
    assert.equal(worker.messages.length, alreadyAborted ? 0 : 1);
  }
});

test('a clone failure rejects only its call, while an initialization refusal closes the worker', async () => {
  const worker = new WorkerHarness(), engine = await ready(worker);
  worker.postMessage = () => { throw new Error('cannot clone'); };
  await assert.rejects(engine.version(), /cannot clone/);
  await engine.dispose();
  const refused = new WorkerHarness();
  const boot = assert.rejects(connectWorkerEngine(refused, bootRequest), /bad engine/);
  refused.reply({ kind: 'init-error', error: 'bad engine' });
  await boot;
  assert.equal(refused.terminated, 1);
});
