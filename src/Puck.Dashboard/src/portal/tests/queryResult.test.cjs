// The results table's text, against DuckDB itself: every value is formatted from the Arrow batch the portal receives
// and compared with `CAST(value AS VARCHAR)` computed by the same engine in the same query, so the oracle is DuckDB's
// own printer rather than an expectation written here.
const assert = require('node:assert/strict');
const { test, before } = require('node:test');
require('./support/register.cjs');
const { openDuckDb, streamOf: stream } = require('./support/duckdb.cjs');
const { collectRows, formatCell } = require('../src/components/data/queryResult.ts');

let connection;

before(async () => {
  connection = (await openDuckDb()).connect();
});

const streamOf = (sql) => stream(connection, sql);

const EXPRESSIONS = [
  "TIMESTAMP '2026-09-23 10:30:00.123456'", "TIMESTAMP '2026-09-23 10:30:00'", "TIMESTAMP '1969-12-31 23:59:59.999999'",
  "TIMESTAMP_S '2026-09-23 10:30:00'", "TIMESTAMP_MS '2026-09-23 10:30:00.123'", "TIMESTAMP_NS '2026-09-23 10:30:00.123456789'",
  "TIMESTAMP_NS '1960-01-01 00:00:00.000000001'", "TIMESTAMP 'infinity'", "TIMESTAMP '-infinity'", "'0044-03-15 (BC)'::DATE::TIMESTAMP",
  "TIMESTAMPTZ '2026-09-23 10:30:00.5+00'", "DATE '2026-09-23'", "DATE '1969-12-31'", "DATE '0001-01-01'", "DATE '0001-12-31 (BC)'",
  "DATE '5877641-06-25'", "TIME '10:30:00.25'", "TIME '00:00:00'", "TIME '23:59:59.999999'",
  "INTERVAL '1 month 2 days 03:04:05.5'", "INTERVAL '-3 hours'", "INTERVAL '14 months'", "INTERVAL '0 seconds'",
  "INTERVAL '-1 year -2 days'", "INTERVAL '36 hours 0.000001 seconds'",
  "-12.345::DECIMAL(10,3)", "0.05::DECIMAL(4,2)", "-0.05::DECIMAL(4,2)", "12345678901234567890.1234567890::DECIMAL(38,10)",
  "0::DECIMAL(5,2)", "170141183460469231731687303715884105727::HUGEINT", "-170141183460469231731687303715884105727::HUGEINT",
  "9007199254740993::BIGINT", "18446744073709551615::UBIGINT", "255::UTINYINT", "(-128)::TINYINT",
  "1.5::DOUBLE", "0.1 + 0.2", "1e20::DOUBLE", "1e-7::DOUBLE", "'NaN'::DOUBLE", "'inf'::DOUBLE", "'-inf'::DOUBLE", "-0.0::DOUBLE",
  "1.1::REAL", "100::REAL", "2.0::DOUBLE", "1e15::DOUBLE", "1e16::DOUBLE", "123456.789::DOUBLE", "0.0001::DOUBLE", "0.00001::DOUBLE", "-2.5e-300::DOUBLE", "1.7976931348623157e308::DOUBLE", "3.4028235e38::REAL", "1.5::REAL", "true", "false",
  "'6ba7b810-9dad-11d1-80b4-00c04fd430c8'::UUID", "'plain text'", "'it''s quoted'", "''",
  "from_hex('AA005C41')", "from_hex('')", "'b'::ENUM('a','b')", "'{\"x\":1}'::JSON",
  "[1, NULL, 3]", "[]::INTEGER[]", "[['a', 'b'], NULL, []]", "['hello, world', 'x']", "[' lead', 'trail ', 'null', 'NULL', '', 'a\\b', 'it''s']", "[1, 2]::INTEGER[2]",
  "{'a': 1, 'b': [1, 2]}", "{'x': NULL, 'y': {'z': 'w'}}", "{'it''s': 1}", "MAP {'k': 1, 'j': NULL}", "MAP {}::MAP(VARCHAR, INTEGER)",
  "[TIMESTAMP '2026-09-23 10:30:00.123456', NULL]", "{'d': DATE '1969-12-31', 'i': INTERVAL '1 day'}",
];

for (const expression of EXPRESSIONS) {
  test(`formats ${expression} as DuckDB prints it`, () => {
    const table = connection.query(`SELECT ${expression} AS value, CAST(${expression} AS VARCHAR) AS oracle`);

    assert.equal(formatCell(table.getChildAt(0), 0), table.getChildAt(1).get(0));
  });
}

test('SQL NULL is null, not text, at the top level', () => {
  const table = connection.query('SELECT NULL::INTEGER AS a, NULL::TIMESTAMP AS b, NULL::INTEGER[] AS c');

  for (let column = 0; column < 3; column++) {
    assert.equal(formatCell(table.getChildAt(column), 0), null);
  }
});

test('columns sharing a name keep their own values', async () => {
  const output = await collectRows(streamOf('SELECT 1 AS x, 2 AS x, 3 AS y'));

  assert.deepEqual(output.columns, ['x', 'x', 'y']);
  assert.deepEqual(output.rows, [['1', '2', '3']]);
});

test('values are read across batch and chunk boundaries', async () => {
  const output = await collectRows(streamOf("SELECT range AS id, TIMESTAMP '2026-01-01' + to_microseconds(range) AS at FROM range(5000)"), 5000);

  assert.equal(output.rows.length, 5000);
  assert.equal(output.truncated, false);
  assert.deepEqual(output.rows[4999], ['4999', '2026-01-01 00:00:00.004999']);
});

test('a large result stops at the row limit and says so', async () => {
  const output = await collectRows(streamOf('SELECT range AS id FROM range(1000000)'), 10);

  assert.equal(output.rows.length, 10);
  assert.equal(output.truncated, true);
  assert.deepEqual(output.rows.at(-1), ['9']);
});

test('a result exactly at the limit is not called truncated', async () => {
  const output = await collectRows(streamOf('SELECT range AS id FROM range(2048)'), 2048);

  assert.equal(output.rows.length, 2048);
  assert.equal(output.truncated, false);
});

test('an empty result still names its columns', async () => {
  const output = await collectRows(streamOf('SELECT 1 AS a, 2 AS b WHERE false'));

  assert.deepEqual(output.columns, ['a', 'b']);
  assert.deepEqual(output.rows, []);
});
