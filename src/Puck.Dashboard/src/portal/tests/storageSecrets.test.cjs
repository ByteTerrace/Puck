// The secrets that carry the user's token into the engine: one per ByteTerrace storage account a query names, scoped to
// that account alone, and never one for any other host.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { STORAGE_API_VERSION } = require('../src/components/data/storage.ts');
const { storageOriginsIn, storageSecretsSql } = require('../src/components/data/storageSecrets.ts');

test('a query names each ByteTerrace storage account once, in order', () => {
  const sql = `
    SELECT * FROM read_parquet('https://bytrcstp002.blob.core.windows.net/u/a.parquet')
    UNION ALL SELECT * FROM read_parquet('https://bytrcstp001.blob.core.windows.net/u/b.parquet')
    UNION ALL SELECT * FROM read_parquet("https://bytrcstp002.blob.core.windows.net/u/c.parquet")`;

  assert.deepEqual(storageOriginsIn(sql), ['https://bytrcstp002.blob.core.windows.net', 'https://bytrcstp001.blob.core.windows.net']);
});

test('no other host gets a secret, however much it looks like storage', () => {
  const sql = [
    "read_csv('https://example.blob.core.windows.net/c/x.csv')",
    "read_csv('https://bytrcstp001.blob.core.windows.net.example.com/x.csv')",
    "read_csv('https://example.com/bytrcstp001.blob.core.windows.net/x.csv')",
    "read_csv('http://bytrcstp001.blob.core.windows.net/c/x.csv')",
    "read_csv('bytrcstp001.blob.core.windows.net/c/x.csv')",
  ].join(' UNION ALL ');

  assert.deepEqual(storageOriginsIn(sql), []);
  assert.equal(storageSecretsSql(storageOriginsIn(sql), 'token'), '');
});

test('each secret is scoped to its account and carries the token as a bearer token', () => {
  const sql = storageSecretsSql(['https://bytrcstp001.blob.core.windows.net', 'https://bytrcstp002.blob.core.windows.net'], 'eyJ.token');

  assert.equal(
    sql,
    [
      `CREATE OR REPLACE SECRET storage_bytrcstp001 (TYPE http, BEARER_TOKEN 'eyJ.token', ` +
        `EXTRA_HTTP_HEADERS MAP {'x-ms-version': '${STORAGE_API_VERSION}', 'Cache-Control': 'no-cache'}, ` +
        `SCOPE 'https://bytrcstp001.blob.core.windows.net/')`,
      `CREATE OR REPLACE SECRET storage_bytrcstp002 (TYPE http, BEARER_TOKEN 'eyJ.token', ` +
        `EXTRA_HTTP_HEADERS MAP {'x-ms-version': '${STORAGE_API_VERSION}', 'Cache-Control': 'no-cache'}, ` +
        `SCOPE 'https://bytrcstp002.blob.core.windows.net/')`,
    ].join(';\n'),
  );
});

test('a token stays inside its literal', () => {
  const sql = storageSecretsSql(['https://bytrcstp001.blob.core.windows.net'], "a'); DROP TABLE t; --");

  assert.match(sql, /BEARER_TOKEN 'a''\); DROP TABLE t; --',/);
});
