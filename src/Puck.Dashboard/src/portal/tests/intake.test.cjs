const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

const {
  MAX_DOCUMENT_BYTES,
  IntakeRefusal,
  checkDocumentSize,
  scanUnsafeIntegerLiterals,
  checkDocument,
} = require('../src/document/intake.ts');

test('checkDocumentSize accepts up to the 2 MB cap and refuses over it', () => {
  checkDocumentSize('{}');
  checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES));
  assert.throws(() => checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES + 1)), IntakeRefusal);
});

test('scanUnsafeIntegerLiterals lets JSON.parse-safe integers and all floats through', () => {
  scanUnsafeIntegerLiterals('{"a":1,"b":-42,"c":9007199254740991,"d":-9007199254740991,"e":0}');
  scanUnsafeIntegerLiterals('{"a":1.5,"b":1e400,"c":-9007199254740999.0,"d":9007199254740999e0}');
  scanUnsafeIntegerLiterals('[1,2,[3,{"x":4}],null,true,false,"9007199254740999"]');
});

test('scanUnsafeIntegerLiterals refuses an out-of-range integer literal by path and offset', () => {
  assert.throws(() => scanUnsafeIntegerLiterals('{"metadata":{"value":9007199254740993}}'), (error) => {
    assert.ok(error instanceof IntakeRefusal);
    assert.equal(error.path, '$.metadata.value');
    assert.equal(error.offset, '{"metadata":{"value":'.length);
    return true;
  });

  assert.throws(() => scanUnsafeIntegerLiterals('{"state":{"world":[{"value":-9007199254740999}]}}'), (error) => {
    assert.equal(error.path, '$.state.world[0].value');
    return true;
  });
});

test('scanUnsafeIntegerLiterals never mistakes a quoted number, or a number inside a string escape, for a literal', () => {
  scanUnsafeIntegerLiterals(JSON.stringify({ text: 'escaped \\" then 9007199254740993 inside a string' }));
  scanUnsafeIntegerLiterals('{"a":"9007199254740993"}');
});

test('checkDocument runs the size cap before the literal scan, and both refusals share IntakeRefusal', () => {
  assert.throws(() => checkDocument('x'.repeat(MAX_DOCUMENT_BYTES + 1)), IntakeRefusal);
  assert.throws(() => checkDocument('{"value":9007199254740993}'), IntakeRefusal);
  checkDocument('{"value":1}');
});
