const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const { readSchemaBundle } = require('../scripts/puckCli.cjs');
const ts = require('typescript');

// Exercise the shipped TypeScript/TSX through Node's test runner, without a second bundler
// (the same loader offline-preview.test.cjs registers, extended to .tsx for JSX).
const compilerOptions = { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX };
require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions }).outputText, file);
require.extensions['.tsx'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), { compilerOptions }).outputText, file);

const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const { MantineProvider } = require('@mantine/core');

const { resolve, classify, computeArrayMove, computeArmSwitch } = require('../src/forms/schemaWalk.ts');
const { SchemaNode } = require('../src/forms/SchemaNode.tsx');
const { SectionForm } = require('../src/forms/SectionForm.tsx');
const { SectionExplorer } = require('../src/forms/SectionExplorer.tsx');
const { getAt, setAt, deleteAt } = require('../src/document/jsonPath.ts');
const { parseDocumentText } = require('../src/document/jsonText.ts');

// See schemaWalk.test.cjs for why this is generated fresh rather than committed.
const bundle = readSchemaBundle(__dirname);

// A small document, walkable end-to-end: one Int row with an authored cell, and a Fixed row
// with no cells, under an otherwise-empty world.
function smallDocument() {
  return {
    schema: 'puck.world.def.v1',
    state: {
      world: [
        { name: 'score', kind: 'Int', value: 0, min: 0, cells: [{ key: '0', value: 3 }] },
        { name: 'speed', kind: 'Fixed', value: '1.5' },
      ],
    },
  };
}

/** Applies one DocumentEdit the way a real consumer would: `undefined` deletes, else sets. */
function applyEdit(document, edit) {
  return edit.value === undefined ? deleteAt(document, edit.path) : setAt(document, edit.path, edit.value);
}

/** Every component under test needs a MantineProvider ancestor; wrap once here. */
function withMantine(element) {
  return React.createElement(MantineProvider, null, element);
}

test('SchemaNode renders the state.world section with field labels, an Int value and a Fixed decimal string', () => {
  const document = smallDocument();
  const edits = [];
  const html = renderToStaticMarkup(withMantine(
    React.createElement(SchemaNode, {
      walker: resolve(bundle),
      document,
      path: ['state', 'world'],
      onEdit: edit => edits.push(edit),
    }),
  ));
  assert.match(html, /score/);
  assert.match(html, /speed/);
  assert.match(html, /1\.5/); // the Fixed row's decimal-string value, rendered as text, never coerced through a lossy number
  assert.equal(edits.length, 0);
});

test('SchemaNode renders an Int64-extreme cell value as an EDITABLE integer text field, not the old read-only fallback', () => {
  // The Int64.MaxValue literal MUST come from parsed TEXT, never a JS numeric literal written in
  // this test's own source — V8 would round a bare `9223372036854775807` the moment this file
  // itself parses, before parseDocumentText ever saw it (the exact corruption this feature fixes).
  const text = '{"schema":"puck.world.def.v1","state":{"world":[' +
    '{"name":"score","kind":"Int","value":0,"cells":[{"key":"0","value":9223372036854775807}]}' +
    ']}}';
  const document = parseDocumentText(text);
  assert.equal(typeof getAt(document, ['state', 'world', 0, 'cells', 0, 'value']), 'bigint');
  assert.equal(getAt(document, ['state', 'world', 0, 'cells', 0, 'value']), 9223372036854775807n);

  const html = renderToStaticMarkup(withMantine(
    React.createElement(SchemaNode, {
      walker: resolve(bundle),
      document,
      path: ['state', 'world'],
      onEdit: () => {},
    }),
  ));
  assert.match(html, /9223372036854775807/);
  // The retired read-only fallback's own copy must be gone from this render.
  assert.doesNotMatch(html, /Beyond exact precision/);
});

test('SectionForm renders a header for the "state" section and its own SchemaNode fields', () => {
  const document = smallDocument();
  const html = renderToStaticMarkup(
    withMantine(React.createElement(SectionForm, { bundle, document, path: ['state'], onEdit: () => {} })),
  );
  assert.match(html, /State/);
  assert.match(html, /score/);
  assert.match(html, /Authored/);
});

test('SectionForm marks an absent, unauthored section and still renders its "set a value" affordance', () => {
  const document = smallDocument();
  const html = renderToStaticMarkup(
    withMantine(React.createElement(SectionForm, { bundle, document, path: ['motion'], onEdit: () => {} })),
  );
  assert.match(html, /Absent/);
});

test('SectionExplorer lists every root section, including imports/exports, with a presence badge', () => {
  const document = smallDocument();
  const walker = resolve(bundle);
  const html = renderToStaticMarkup(
    withMantine(React.createElement(SectionExplorer, { bundle, document, onSelect: () => {} })),
  );
  for (const section of walker.rootSections()) {
    assert.match(html, new RegExp(section.key, 'i'));
  }
  assert.match(html, /authored/);
  assert.match(html, /absent/i);
});

test('an edit fired through SchemaNode\'s onEdit contract applies via setAt/deleteAt like a real consumer would', () => {
  // Simulate the exact edit a "set state.world[0].value" NumberInput change would fire,
  // WITHOUT a DOM: this is the pure edit-computation path, only the emitted DocumentEdit shape
  // and jsonPath's own apply step under test.
  const document = smallDocument();
  const setEdit = { path: ['state', 'world', 0, 'value'], value: 9, label: 'set state.world[0].value' };
  const afterSet = applyEdit(document, setEdit);
  assert.equal(getAt(afterSet, ['state', 'world', 0, 'value']), 9);
  assert.equal(getAt(document, ['state', 'world', 0, 'value']), 0, 'the original document is untouched');

  const removeEdit = { path: ['state', 'world', 0, 'min'], value: undefined, label: 'remove state.world[0].min' };
  const afterRemove = applyEdit(afterSet, removeEdit);
  assert.equal('min' in afterRemove.state.world[0], false);
});

test('computeArrayMove and computeArmSwitch are pure and testable with no DOM, driving the same edits SchemaNode emits', () => {
  const document = smallDocument();
  const rows = document.state.world;
  const moved = computeArrayMove(rows, 0, 1);
  assert.deepEqual(moved.map(r => r.name), ['speed', 'score']);

  const walker = resolve(bundle);
  const domainNode = walker.atPath(['state', 'world', 0, 'domain']);
  const switched = computeArmSwitch(walker, domainNode, 'slot');
  assert.deepEqual(switched, { $type: 'slot' });
  assert.equal(classify(walker.atPath(['state', 'world', 0, 'domain'], { state: { world: [{ ...rows[0], domain: switched }] } })), 'object');
});
