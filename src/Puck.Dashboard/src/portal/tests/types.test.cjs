const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

function findRepoRoot(start) {
  let dir = start;
  while (!fs.existsSync(path.join(dir, 'CLAUDE.md'))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error(`Could not locate the repository root above ${start}.`);
    dir = parent;
  }
  return dir;
}

function loadSchemaBundle() {
  const puckExe = path.join(repoRoot, 'src', 'Puck.Cli', 'publish', 'puck.exe');
  if (!fs.existsSync(puckExe)) {
    throw new Error(`puck.exe is not published at ${puckExe}. Run: dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish`);
  }
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'puck-types-defs-'));
  try {
    const bundlePath = path.join(tmpDir, 'bundle.json');
    execFileSync(puckExe, ['schema', '--bundle', bundlePath], { stdio: 'pipe' });
    return JSON.parse(fs.readFileSync(bundlePath, 'utf8'));
  } finally {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  }
}

const repoRoot = findRepoRoot(__dirname);
const generatedPath = path.join(__dirname, '..', 'src', 'document', 'worldDefinition.generated.ts');
const generated = fs.readFileSync(generatedPath, 'utf8');
const bundle = loadSchemaBundle();
const bundleDefNames = new Set(Object.keys(bundle['$defs'] ?? {}));
const topLevelExportNames = (generated.match(/^export (?:type|interface) (\w+)/gm) ?? []).map(line => line.replace(/^export (?:type|interface) /, ''));

test('worldDefinition.generated.ts carries a GENERATED header naming the source bundle', () => {
  assert.match(generated, /^\/\/ GENERATED FILE — do not hand-edit\.$/m);
  assert.match(generated, /Source bundle: schemaVersion=puck\.world\.def\.v1 generator=\S+/);
});

test('worldDefinition.generated.ts exports the top-level WorldDefinition type', () => {
  assert.match(generated, /^export type WorldDefinition = \{/m);
});

test('every $defs entry in the bundle names its own top-level export — WorldDefinition, WorldStateRow, LatticeTopology among them', () => {
  // The bundle's own $defs pool (WorldSchema.Bundle) is the authority on what MUST get a named
  // export: every key there is a titled shape (including VariantN for differing nested constraints),
  // so every one of them has to appear as its own
  // top-level `export type`/`export interface` by that exact name, not folded into an anonymous
  // literal at each of its call sites.
  assert.ok(bundleDefNames.size > 100, `expected the bundle to carry a substantial $defs pool, got ${bundleDefNames.size}`);
  assert.ok(bundleDefNames.has('WorldStateRow'));
  assert.ok(bundleDefNames.has('LatticeTopology'));

  const topLevelExports = new Set(topLevelExportNames);
  const missing = [...bundleDefNames].filter(name => !topLevelExports.has(name));
  assert.deepEqual(missing, [], `every $defs key must name a top-level export; missing: ${missing.join(', ')}`);
  assert.ok(topLevelExports.has('WorldDefinition'), 'the root export itself must also be present');
});

test('WorldDefinition.state.world\'s element type is the named WorldStateRow, not an inline literal', () => {
  assert.match(generated, /^\s*world\?:\s*WorldStateRow\[\]\s*\|\s*null;/m);
});

test('no exported name carries an unexplained numeric disambiguation suffix', () => {
  // A suffixed export name is legitimate for exactly two reasons:
  //  1. It IS ITSELF one of the bundle's own $defs keys — a genuine, distinct C# shape whose
  //     name happens to end in a digit: either the name always did (DocumentVector2/3,
  //     ClosedBitset256), or ChooseDefName's own pre-existing nullable/non-nullable collision
  //     fallback settled on it (ActionPredicateNullable2 — a REAL second shape, not a duplicate
  //     of ActionPredicateNullable; see WorldSchema.cs's own ChooseDefName remark) — either way
  //     the FIRST test above already asserts every such key gets exactly this export.
  //  2. It is json-schema-to-typescript's OWN allOf-decomposition helper: WorldStateRow and
  //     WorldRenderExtensionEntry are the only two $defs whose OWN content mixes "allOf" with
  //     "properties"/"anyOf" (StateRowJsonConverter's hand-built kind-conditional shape, and the
  //     shipped post-render-extension splice — see WorldSchema.Bundle's own remark on
  //     BuildReferenceSite); the compiler decomposes such a schema into an intersection of
  //     internally-numbered helpers (`export type X = X1 & X2;`), with the PUBLIC name (X)
  //     itself staying clean and correctly shared everywhere it is used.
  // Any OTHER numeric-suffixed export is the json-schema-to-typescript duplicate-naming bug
  // Bundle()'s own $ref-wrapping (BuildReferenceSite) exists to avoid — a regression, not a
  // legitimate name.
  const allOfHelperBases = new Set(['WorldStateRow', 'WorldRenderExtensionEntry']);
  const suffixed = topLevelExportNames.filter(name => /[0-9]+$/.test(name));
  const unexpected = suffixed.filter(name => {
    if (bundleDefNames.has(name)) return false;
    const base = name.replace(/[0-9]+$/, '');
    return !allOfHelperBases.has(base);
  });
  assert.deepEqual(unexpected, [], `unexpected numeric-suffixed export(s): ${unexpected.join(', ')}`);
});

test('a state row declares kind as the PascalCase union Int | Fixed | Bool | Text', () => {
  assert.match(generated, /kind: "Int" \| "Fixed" \| "Bool" \| "Text";/);
});

test('a state row\'s cells[].key is a plain string and cells[].value is the named ShapeNonNullable union', () => {
  const cellsBlockMatch = generated.match(/cells\?:\s*\{\s*key: string;\s*value: ShapeNonNullable;/);
  assert.ok(cellsBlockMatch, 'expected a cells entry shaped { key: string; value: ShapeNonNullable; ... }');
  assert.match(generated, /^export type ShapeNonNullable = number \| string \| boolean;/m);
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
