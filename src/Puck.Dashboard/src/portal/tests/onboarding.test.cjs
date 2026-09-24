// The host's self-onboarding (shared/onboarding.ts), run against a scripted server with a 1 ms poll.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { lastValueFrom, toArray } = require('rxjs');
const { onboardingProgress } = require('../../shared/onboarding.ts');

function server(script) {
  const requests = [];
  return {
    request: async (method) => {
      requests.push(method);
      const next = script.shift();
      if (next instanceof Error) throw next;
      return { state: next ?? 'Provisioning' };
    },
    requests,
  };
}
const fast = { attempts: 5, intervalMilliseconds: 1 };

test('an account that is already provisioned is ready after the unconditional POST', async () => {
  const { request, requests } = server(['Ready']);
  assert.deepEqual(await lastValueFrom(onboardingProgress(request, fast).pipe(toArray())), ['ready']);
  assert.deepEqual(requests, ['POST']);
});

test('provisioning polls, re-POSTs whenever the account is not onboarded, and ends ready (Migrating counts)', async () => {
  const { request, requests } = server(['Provisioning', 'NotOnboarded', 'Provisioning', 'Migrating']);
  assert.deepEqual(await lastValueFrom(onboardingProgress(request, fast).pipe(toArray())), ['onboarding', 'ready']);
  assert.deepEqual(requests, ['POST', 'GET', 'POST', 'GET']);
});

test('exhausted polling fails by name, and a rejected request fails with its own reason', async () => {
  const slow = server([]);
  await assert.rejects(lastValueFrom(onboardingProgress(slow.request, fast)), /did not complete within the expected time frame/);
  assert.deepEqual(slow.requests, ['POST', 'GET', 'GET', 'GET', 'GET', 'GET']);
  const rejected = server(['Provisioning', new Error('Onboarding request was rejected (HTTP 403).')]);
  await assert.rejects(lastValueFrom(onboardingProgress(rejected.request, fast)), /HTTP 403/);
});

// The lifecycle around that protocol (onboardingMachine): what the host starts and the portal shows and retries.
const { createActor, waitFor } = require('xstate');
const { onboardingMachine } = require('../../shared/onboarding.ts');

function onboardingActor(script) {
  const scripted = server(script);
  const actor = createActor(onboardingMachine, { input: { polling: fast, request: scripted.request } }).start();
  return { actor, requests: scripted.requests };
}

test('setup waits for sign-in, reports polling as onboarding, and ends ready; later sign-in notices change nothing', async () => {
  const { actor, requests } = onboardingActor(['Provisioning', 'Provisioning', 'Ready']);
  assert.ok(actor.getSnapshot().matches('signedOut'));
  assert.deepEqual(requests, [], 'nothing is asked before sign-in');

  assert.equal(actor.getSnapshot().hasTag('busy'), false);
  actor.send({ type: 'SIGNED_IN' });
  assert.ok(actor.getSnapshot().matches({ working: 'checking' }));
  assert.equal(actor.getSnapshot().hasTag('busy'), true, 'the account controls read the tag, not the state name');
  await waitFor(actor, (s) => s.matches({ working: 'onboarding' }));
  const ready = await waitFor(actor, (s) => s.matches('ready'));
  assert.equal(ready.hasTag('busy'), false);
  assert.equal(ready.can({ type: 'RETRY' }), false, 'nothing to retry once ready');
  actor.send({ type: 'SIGNED_IN' });
  assert.ok(actor.getSnapshot().matches('ready'));
  assert.deepEqual(requests, ['POST', 'GET', 'GET']);
  actor.stop();
});

test('a failure names its reason, and a retry runs the protocol again from its POST', async () => {
  const { actor, requests } = onboardingActor([new Error('Onboarding request was rejected (HTTP 403).'), 'Ready']);
  actor.send({ type: 'SIGNED_IN' });
  const failed = await waitFor(actor, (s) => s.matches('failed'));
  assert.equal(failed.context.error, 'Onboarding request was rejected (HTTP 403).');
  assert.equal(failed.can({ type: 'RETRY' }), true, 'the account controls offer a retry by asking the machine');
  assert.equal(failed.hasTag('busy'), false);

  actor.send({ type: 'RETRY' });
  assert.equal(actor.getSnapshot().context.error, null, 'a retry clears the old reason');
  await waitFor(actor, (s) => s.matches('ready'));
  assert.deepEqual(requests, ['POST', 'POST']);
  actor.stop();
});
