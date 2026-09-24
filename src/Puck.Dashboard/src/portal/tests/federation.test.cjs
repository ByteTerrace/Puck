// The federation share against both applications' manifests: a runtime dependency the host and portal both declare
// is one instance per page, and every shared package is one the host provides and the portal declares.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const path = require('node:path');
require('./support/register.cjs');
const { federationShare } = require('../../build/federationShare.ts');

const host = require(path.join(__dirname, '..', '..', 'host', 'package.json'));
const portal = require(path.join(__dirname, '..', 'package.json'));
const portalDeclared = { ...portal.devDependencies, ...portal.dependencies };

test('every runtime dependency both applications declare is a strict-version singleton', () => {
  const both = Object.keys(host.dependencies).filter((name) => name in portal.dependencies);
  assert.ok(both.includes('xstate') && both.includes('rxjs'), 'the actor the host hands across needs its library shared');
  for (const name of both) {
    assert.deepEqual(federationShare[name], { singleton: true, strictVersion: true }, `${name} is not shared`);
  }
});

test('every shared package is provided by the host and declared by the portal in the same range', () => {
  for (const name of Object.keys(federationShare)) {
    assert.ok(name in host.dependencies, `the host does not provide ${name}`);
    assert.ok(name in portalDeclared, `the portal does not declare ${name}`);
    assert.equal(portalDeclared[name], host.dependencies[name], `${name} is declared in different ranges`);
  }
});
