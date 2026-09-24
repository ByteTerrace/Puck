const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
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

test('cost analysis travels through the worker with its exact counts', async () => {
  const worker = new WorkerHarness(), engine = await ready(worker);
  const pending = engine.costs('current');
  const call = worker.messages.at(-1);
  assert.equal(call.method, 'costs');
  assert.deepEqual(call.args, ['current']);
  worker.reply({ kind: 'result', id: call.id, ok: true, value: { stepAllowanceCycles: 9223372036854775807n } });
  assert.equal((await pending).stepAllowanceCycles, 9223372036854775807n);
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

test('closing detaches every listener it attached, from the worker and the abort signal', async () => {
  const counted = (target) => {
    const live = new Map();
    const add = target.addEventListener.bind(target), remove = target.removeEventListener.bind(target);
    target.addEventListener = (type, listener, options) => { live.set(listener, type); add(type, listener, options); };
    target.removeEventListener = (type, listener, options) => { live.delete(listener); remove(type, listener, options); };
    return live;
  };
  for (const ending of ['dispose', 'error', 'abort']) {
    const worker = new WorkerHarness(), controller = new AbortController();
    const workerListeners = counted(worker), signalListeners = counted(controller.signal);
    const boot = connectWorkerEngine(worker, bootRequest, controller.signal);
    if (ending === 'abort') {
      controller.abort();
      await assert.rejects(boot, /cancelled/);
    } else {
      worker.reply({ kind: 'ready' });
      const engine = await boot;
      const pending = assert.rejects(engine.rows('h'), /disposed|failed/);
      if (ending === 'dispose') await engine.dispose(); else worker.dispatchEvent(new Event('error'));
      await pending;
    }
    assert.equal(workerListeners.size, 0, `${ending}: worker listeners left: ${[...workerListeners.values()].join(', ')}`);
    assert.equal(signalListeners.size, 0, `${ending}: abort-signal listeners left`);
    assert.equal(worker.terminated, 1);
  }
});

test('the language channel opens once and pumps the worker\'s lsp and lspIdle as ordinary calls', async () => {
  const worker = new WorkerHarness(), engine = await ready(worker);
  const channel = engine.languageServer();
  assert.throws(() => engine.languageServer(), /already open/);
  const received = [];
  channel.messages().subscribe((message) => received.push(message.id));
  const nextCall = async (method) => {
    for (let turn = 0; turn < 50; turn++) {
      const call = worker.messages.find((message) => message.kind === 'call' && message.method === method && !message.answered);
      if (call) { call.answered = true; return call; }
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    throw new Error(`no '${method}' call`);
  };

  channel.send('{"jsonrpc":"2.0","id":1,"method":"initialize"}');
  const lsp = await nextCall('lsp');
  assert.deepEqual(lsp.args, ['{"jsonrpc":"2.0","id":1,"method":"initialize"}']);
  worker.reply({ kind: 'result', id: lsp.id, ok: true, value: [{ jsonrpc: '2.0', id: 1 }] });
  const idle = await nextCall('lspIdle');
  worker.reply({ kind: 'result', id: idle.id, ok: true, value: { ran: false, pending: false, messages: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(received, [1]);
  assert.equal(worker.messages.filter((message) => message.method === 'lspIdle').length, 1, 'the pump stops once nothing is pending');

  channel.close();
  assert.doesNotThrow(() => engine.languageServer().close(), 'a closed channel can be opened again');
  await engine.dispose();
});

