// The pinned DuckDB extensions (duckdb-extensions.json) against the engine the portal installs: an extension only
// loads into the DuckDB version it was built for, so upgrading @duckdb/duckdb-wasm without re-pinning fails here.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { openDuckDb } = require('./support/duckdb.cjs');
const manifest = require('../duckdb-extensions.json');

test('the pinned extensions are built for the DuckDB version the engine reports', async () => {
  const version = (await openDuckDb()).connect().query('SELECT version() AS v').getChildAt(0).get(0);

  assert.equal(manifest.version, version);
});

test('every extension the engine setup loads is pinned for both bundles the portal ships', () => {
  for (const platform of ['wasm_eh', 'wasm_mvp']) {
    for (const extension of ['httpfs', 'icu', 'json', 'parquet']) {
      assert.match(manifest.files[`${platform}/${extension}`] ?? '', /^[0-9a-f]{64}$/, `${platform}/${extension}`);
    }
  }
});
