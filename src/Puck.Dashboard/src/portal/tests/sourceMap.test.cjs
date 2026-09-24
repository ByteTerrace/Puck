// The way back from compiled IR to source: a pointer resolves through its nearest mapped ancestor, a rule resolves by
// its name, and a position in the compiled JSON text names the value under it.
const assert = require('node:assert/strict');
const { test } = require('node:test');
require('./support/register.cjs');
const { EditorState } = require('@codemirror/state');
const { json } = require('@codemirror/lang-json');
const { spanAt, rulePath, ruleSpan } = require('../src/authoring/sourceMap.ts');
const { jsonPointerAt } = require('../src/components/world/authoring/jsonPointerAt.ts');
const { rootSections } = require('../src/components/world/authoring/CompiledViews.tsx');
const { outlineRows } = require('../src/components/world/authoring/SourceEditor.tsx');
const { serializeDocumentText } = require('../src/document/jsonText.ts');

const span = (line) => ({ path: 'counter.puck', line, column: 1, length: 4, module: null });

test('a pointer resolves to its own span, else its nearest mapped ancestor, else nothing', () => {
  const map = { '/rules/0': span(11), '/state': span(5), '/state/world/0/value': span(7) };
  assert.equal(spanAt(map, '/state/world/0/value').line, 7);
  assert.equal(spanAt(map, '/state/world/0/name').line, 5, 'an unmapped value takes its section\'s span');
  assert.equal(spanAt(map, '/rules/0/gate/value').line, 11);
  assert.equal(spanAt(map, '/documentId'), null);
  assert.equal(spanAt({ '': span(1) }, '/anything').line, 1, 'a mapped root answers for everything');
});

test('a rule resolves by name to its own entry in the document\'s rules', () => {
  const document = { rules: [{ name: 'first' }, { name: 'second' }] };
  assert.deepEqual(rulePath(document, 'second'), ['rules', 1]);
  assert.equal(rulePath(document, 'absent'), null);
  assert.equal(rulePath({}, 'first'), null);
  assert.equal(ruleSpan(document, { '/rules/1': span(20), '/rules/1/name': span(21), '/rules': span(9) }, 'second').line, 20, 'the rule\'s own span');
  assert.equal(ruleSpan(document, { '/rules': span(9) }, 'second').line, 9, 'else its nearest mapped ancestor');
  assert.equal(ruleSpan(document, { '/rules/0': span(10) }, 'second'), null, 'no span when nothing above the rule is mapped');
  assert.equal(ruleSpan(document, { '/rules': span(9) }, 'absent'), null, 'a rule this document lacks resolves to nothing');
});

test('the pointer at a position in compiled JSON text names the value under it, keys included', () => {
  const value = { documentId: 'counter', state: { world: [{ name: 'score', value: 0 }, { name: 'marks', value: 9223372036854775807n }] } };
  const text = serializeDocumentText(value);
  const state = EditorState.create({ doc: text, extensions: [json()] });
  const at = (needle, offset = 1) => jsonPointerAt(state, text.indexOf(needle) + offset);
  assert.equal(at('"counter"'), '/documentId');
  assert.equal(at('"documentId"'), '/documentId', 'a key names its property\'s value');
  assert.equal(at('"marks"'), '/state/world/1/name');
  assert.equal(at('9223372036854775807'), '/state/world/1/value');
  assert.equal(at('"world"'), '/state/world');
  assert.equal(jsonPointerAt(state, 0), '');
});

test('root sections come from the schema bundle in declaration order', () => {
  const sections = rootSections({ properties: { state: { title: 'State', description: 'Rows.' }, rules: {} } });
  assert.deepEqual(sections, [
    { key: 'state', title: 'State', description: 'Rows.' },
    { key: 'rules', title: undefined, description: undefined },
  ]);
  assert.deepEqual(rootSections(null), []);
});

test('an outline flattens hierarchical symbols depth first and reads flat ones too', () => {
  const rows = outlineRows([
    { name: 'state', selectionRange: { start: { line: 4, character: 0 } }, children: [{ name: 'score', selectionRange: { start: { line: 6, character: 4 } } }] },
    { name: 'score-up', location: { range: { start: { line: 10, character: 0 } } } },
  ]);
  assert.deepEqual(rows.map((row) => [row.name, row.depth, row.start.line]), [['state', 0, 4], ['score', 1, 6], ['score-up', 0, 10]]);
  assert.deepEqual(outlineRows(null), []);
});
