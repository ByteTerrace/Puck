const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');

const generatedPath = path.join(__dirname, '..', 'src', 'document', 'worldDefinition.generated.ts');
const generated = fs.readFileSync(generatedPath, 'utf8');

test('worldDefinition.generated.ts carries a GENERATED header naming the source bundle', () => {
  assert.match(generated, /^\/\/ GENERATED FILE — do not hand-edit\.$/m);
  assert.match(generated, /Source bundle: schemaVersion=puck\.world\.def\.v1 commit=[0-9a-f]+ generator=\S+/);
});

test('worldDefinition.generated.ts exports the top-level WorldDefinition type', () => {
  assert.match(generated, /^export type WorldDefinition = \{/m);
});

test('worldDefinition.generated.ts exports every $defs type reachable via $ref (the bundle carries none of its own, so this is vacuously satisfied by the file compiling at all)', () => {
  // The current puck.world.def.v1 bundle inlines its whole tree with no top-level "$defs" —
  // json-schema-to-typescript instead hoists every internally $ref'd subschema (e.g. camera rig
  // operations, screen sources) into its own top-level `export type`. Assert that hoisting
  // actually happened rather than everything collapsing into one inline literal.
  const topLevelExports = generated.match(/^export (?:type|interface) \w+/gm) ?? [];
  assert.ok(topLevelExports.length > 10, `expected many hoisted top-level exports, got ${topLevelExports.length}`);
});

test('a state row declares kind as the PascalCase union Int | Fixed | Bool | Text', () => {
  assert.match(generated, /kind: "Int" \| "Fixed" \| "Bool" \| "Text";/);
});

test('a state row\'s cells[].key is typed as a plain string', () => {
  const cellsBlockMatch = generated.match(/cells\?:\s*\{\s*key: string;\s*value: number \| string \| boolean;/);
  assert.ok(cellsBlockMatch, 'expected a cells entry shaped { key: string; value: ...; ... }');
});

test('the generated file parses as valid TypeScript with no syntax errors (full semantic checking happens in "npm run build"\'s tsc -b)', () => {
  const ts = require('typescript');
  const result = ts.transpileModule(generated, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    reportDiagnostics: true,
  });
  const errors = (result.diagnostics ?? []).filter((d) => d.category === ts.DiagnosticCategory.Error);
  assert.deepEqual(errors.map((e) => ts.flattenDiagnosticMessageText(e.messageText, '\n')), []);
});
