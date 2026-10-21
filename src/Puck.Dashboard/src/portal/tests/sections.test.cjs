const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { sectionFromLocation, sectionPath } = require('../src/shell/sections.ts');

test('docs and Puck subdomains open their website sections directly', () => {
  assert.equal(sectionFromLocation({ hostname: 'docs.byteterrace.com', pathname: '/' }), 'docs');
  assert.equal(sectionFromLocation({ hostname: 'puck.byteterrace.com', pathname: '/' }), 'studio');
});
test('explicit page routes work on every hostname and survive navigation between sections', () => {
  for (const hostname of ['byteterrace.com', 'docs.byteterrace.com', 'puck.byteterrace.com', 'localhost']) {
    for (const section of ['docs', 'studio', 'audit', 'data']) {
      assert.equal(sectionFromLocation({ hostname, pathname: sectionPath(section, hostname) }), section);
    }
  }
});
test('page prefixes require a path boundary', () => {
  assert.equal(sectionFromLocation({ hostname: 'byteterrace.com', pathname: '/docs/api' }), 'docs');
  assert.equal(sectionFromLocation({ hostname: 'byteterrace.com', pathname: '/docs-extra' }), 'studio');
  assert.equal(sectionFromLocation({ hostname: 'byteterrace.com', pathname: '/database' }), 'studio');
});
