const assert = require('node:assert/strict');
const { test } = require('node:test');

require('./support/register.cjs');

const { MAX_DOCUMENT_BYTES, IntakeRefusal, checkDocumentSize } = require('../src/document/intake.ts');

test('checkDocumentSize accepts up to the 2 MB cap and refuses over it', () => {
  checkDocumentSize('{}');
  checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES));
  assert.throws(() => checkDocumentSize('x'.repeat(MAX_DOCUMENT_BYTES + 1)), IntakeRefusal);
});
