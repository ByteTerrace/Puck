// Exercises document/jsonText.ts's real JSON.parse/JSON.stringify calls (not a mock) — the point
// of this module is that the RUNTIME actually carries the "JSON.parse source access" proposal and
// JSON.rawJSON, so a test that stubs either would prove nothing about the environment this ships to.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const {
  hasJsonSourceTextSupport,
  parseDocumentText,
  serializeDocumentText,
  JsonSourceTextUnsupportedError,
} = require('../src/document/jsonText.ts');

test('this Node build actually carries JSON.parse source access + JSON.rawJSON', () => {
  // Not a mock: if this ever goes false on the Node version this repository targets, every other
  // test in this file would be proving nothing — fail loudly here first, by name.
  assert.equal(hasJsonSourceTextSupport(), true);
});

test('parseDocumentText preserves an Int64 extreme as a bigint; JSON.parse alone would round it', () => {
  const text = '{"min":-9223372036854775808,"max":9223372036854775807,"mid":42}';
  const value = parseDocumentText(text);
  assert.equal(typeof value.min, 'bigint');
  assert.equal(value.min, -9223372036854775808n);
  assert.equal(typeof value.max, 'bigint');
  assert.equal(value.max, 9223372036854775807n);
  // A safe-range integer stays a plain JS number, never gets bigint-ified needlessly.
  assert.equal(typeof value.mid, 'number');
  assert.equal(value.mid, 42);

  // The corruption this module exists to fix: JSON.parse alone silently rounds the same literal
  // to the nearest representable double — compare via BigInt (exact for whatever double it lands
  // on), since a same-magnitude BigInt LITERAL in this very test file would round identically at
  // V8's own source-parse time and prove nothing.
  assert.notEqual(BigInt(JSON.parse(text).max), 9223372036854775807n);
});

test('parseDocumentText leaves floats and exponents as plain numbers, never bigint', () => {
  const value = parseDocumentText('{"a":1.5,"b":1e400,"c":-9007199254740999.0,"d":9007199254740999e0}');
  assert.equal(typeof value.a, 'number');
  assert.equal(typeof value.b, 'number');
  assert.equal(typeof value.c, 'number');
  assert.equal(typeof value.d, 'number');
});

test('parseDocumentText never mistakes a quoted number, or a number inside a string, for a literal', () => {
  const value = parseDocumentText('{"a":"9223372036854775807","b":"escaped \\" then 9223372036854775807 inside a string"}');
  assert.equal(typeof value.a, 'string');
  assert.equal(value.a, '9223372036854775807');
  assert.equal(typeof value.b, 'string');
});

test('parseDocumentText finds an Int64 extreme nested in arrays and objects alike', () => {
  const value = parseDocumentText('{"state":{"world":[{"min":-9223372036854775808,"max":9223372036854775807}]}}');
  assert.equal(value.state.world[0].min, -9223372036854775808n);
  assert.equal(value.state.world[0].max, 9223372036854775807n);
});

test('serializeDocumentText writes a bigint leaf back out as a bare integer literal, 2-space indented', () => {
  const text = serializeDocumentText({ max: 9223372036854775807n, min: -9223372036854775808n, mid: 42 });
  assert.ok(text.includes('"max": 9223372036854775807'), text);
  assert.ok(text.includes('"min": -9223372036854775808'), text);
  assert.ok(text.includes('"mid": 42'), text);
  assert.ok(!text.includes('"9223372036854775807"'), 'a bigint must serialize unquoted');
});

test('parseDocumentText then serializeDocumentText round-trips an Int64 extreme byte-for-byte', () => {
  // The raw (unquoted) Int64 literals, 2-space indented, as an authored document would carry them.
  const text = '{\n  "a": 1,\n  "state": {\n    "world": [\n      {\n        "name": "row",\n        "min": -9223372036854775808,\n        "max": 9223372036854775807\n      }\n    ]\n  }\n}';
  const value = parseDocumentText(text);
  const roundTripped = serializeDocumentText(value);
  assert.equal(roundTripped, text);
});

test('an unrelated, untouched Int64 extreme survives a round trip through an edited sibling field', () => {
  const text = '{"state":{"world":[{"name":"row","min":-9223372036854775808,"max":9223372036854775807}]},"metadata":{"note":"before"}}';
  const value = parseDocumentText(text);
  value.metadata.note = 'after';
  const roundTripped = serializeDocumentText(value);
  assert.ok(roundTripped.includes('9223372036854775807'));
  assert.ok(roundTripped.includes('-9223372036854775808'));
  assert.ok(roundTripped.includes('"after"'));
  // Round-tripping again reproduces the identical text — the fixed point this module promises.
  assert.equal(serializeDocumentText(parseDocumentText(roundTripped)), roundTripped);
});

test('JsonSourceTextUnsupportedError is exported and named, for the runtime-gap refusal', () => {
  assert.equal(typeof JsonSourceTextUnsupportedError, 'function');
  const error = new JsonSourceTextUnsupportedError();
  assert.equal(error.name, 'JsonSourceTextUnsupportedError');
  assert.ok(error instanceof Error);
});
