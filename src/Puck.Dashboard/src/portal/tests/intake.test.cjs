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
  checkDocument,
} = require('../src/document/intake.ts');

test('checkDocumentSize accepts up to the 2 MB cap and refuses over it', () => {
  checkDocumentSize('{}');
  checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES));
  assert.throws(() => checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES + 1)), IntakeRefusal);
});

// An integer literal outside Number.MAX_SAFE_INTEGER is no longer an intake refusal — see
// document/jsonText.ts's own remarks: parseDocumentText/serializeDocumentText preserve one
// exactly as a bigint, so there is nothing left here to refuse it for (see tests/jsonText.test.cjs
// and tests/studioMachine.test.cjs's own Int64 fidelity coverage).
test('checkDocument runs only the size cap now that Int64 literals are representable', () => {
  assert.throws(() => checkDocument('x'.repeat(MAX_DOCUMENT_BYTES + 1)), IntakeRefusal);
  checkDocument('{"value":9223372036854775807}');
  checkDocument('{"value":1}');
});
