// The SQL the storage page writes for a file or share link, parsed and run by the real engine: a quote in a name stays
// inside its literal, and a link from a URL fragment is only trusted when it points at ByteTerrace storage.
const assert = require('node:assert/strict');
const { test, before } = require('node:test');
require('./support/register.cjs');
const { openDuckDb } = require('./support/duckdb.cjs');
const { blobUrl, buildQuerySql, describeShareUri, sqlStringLiteral } = require('../src/components/data/storage.ts');

let db;
let connection;

before(async () => {
  db = await openDuckDb();
  connection = db.connect();
});

test('a literal reads back as exactly the text it quotes, whatever the text contains', () => {
  const texts = ["it's.csv", "''", "');SELECT 42;--", "back\\slash", 'double"quote', 'line\nbreak', '', 'ünïcödé ✓'];

  for (const text of texts) {
    const table = connection.query(`SELECT ${sqlStringLiteral(text)} AS value`);

    assert.equal(table.numRows, 1);
    assert.equal(table.getChildAt(0).get(0), text);
  }
});

test('the starter query for a file with a quote in its name reads that file', () => {
  db.registerFileText("it's.csv", 'a,b\n1,x\n2,y\n');
  const table = connection.query(buildQuerySql('read_csv_auto', "it's.csv"));

  assert.equal(table.numRows, 2);
});

test('a crafted link stays one string: the query names one file and runs nothing else', () => {
  const crafted = "https://bytrcstp001.blob.core.windows.net/c/x.csv?q=');CREATE TABLE injected AS SELECT 1;--";
  const sql = buildQuerySql('read_csv_auto', crafted);

  assert.throws(() => connection.query(sql), /IO Error|HTTP|No files found/, 'only the one read, which cannot reach storage here');
  assert.equal(connection.query("SELECT count(*) AS n FROM duckdb_tables() WHERE table_name = 'injected'").getChildAt(0).get(0), 0n);
});

test('only ByteTerrace storage links are trusted', () => {
  assert.equal(describeShareUri('https://bytrcstp001.blob.core.windows.net/user/private/a.csv?se=2026-09-24').isByteTerrace, true);
  assert.equal(describeShareUri('https://bytrcstp042.blob.core.windows.net/user/a.parquet').isByteTerrace, true);
  assert.equal(describeShareUri('https://evil.example/a.csv').isByteTerrace, false);
  assert.equal(describeShareUri('https://bytrcstp001.blob.core.windows.net.evil.example/a.csv').isByteTerrace, false);
  assert.equal(describeShareUri('not a url').isByteTerrace, false);
});

test('a blob URL encodes each path segment of the blob name', () => {
  assert.equal(
    blobUrl('https://bytrcstp001.blob.core.windows.net', 'user', 'private/q?#50% off.csv'),
    'https://bytrcstp001.blob.core.windows.net/user/private/q%3F%2350%25%20off.csv',
  );
});
