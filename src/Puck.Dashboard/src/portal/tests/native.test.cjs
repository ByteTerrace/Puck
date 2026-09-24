// Exercises the native/* TypeScript FACADE (engineHost.ts, inlineHost.ts, engineBoot.ts) against
// the real Puck.World.Browser AppBundle under Node — not the raw [JSExport] strings
// tests/engine-wasm.test.cjs drives directly, but the bigint-typed, ParseResult/RowInfo-shaped
// WorldEngine surface every consumer of this package actually calls. A marshalling regression in
// wrapRawExports (a decimal-string long that stays a string, a ParseResult arm decoded wrong)
// fails here even when the raw exports it wraps are themselves correct.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');

require('./support/register.cjs');

const { createEngineHost } = require('../src/native/engineHost.ts');
const { bootEngineFromLocalBundle } = require('../src/native/engineBoot.ts');

function repositoryRoot() {
  let dir = __dirname;
  while (true) {
    if (fs.existsSync(path.join(dir, 'Puck.slnx'))) return dir;
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error('could not find Puck.slnx walking up from ' + __dirname);
    dir = parent;
  }
}

const root = repositoryRoot();
const appBundleDir = path.join(root, 'src', 'Puck.World.Browser', 'bin', 'Release', 'net10.0', 'browser-wasm', 'AppBundle');
const mainMjs = path.join(appBundleDir, 'main.mjs');
const { officialTreeMissing, officialDocument, officialSources, TICTACTOE_ROOT } = require('./support/officialTree.cjs');
const { readWorkspaceFixture } = require('./support/studioFixture.cjs');
const { EngineCapabilityMissing } = require('../src/native/engineTypes.ts');
const { sourceUri } = require('../src/document/sourcePaths.ts');
const { parseDocumentText } = require('../src/document/jsonText.ts');
const { pathToPointer } = require('../src/document/jsonPath.ts');
const { rulePath, ruleSpan } = require('../src/authoring/sourceMap.ts');

// A test that needs a compiled world skips itself by name when no official tree has been built.
const needsTree = (t) => { const missing = officialTreeMissing(); if (missing) t.skip(missing); return !missing; };

// A test of the source exports skips itself by name on an engine build that has none.
async function needsSourceExports(t, engine) {
  try {
    await engine.mountSources(readWorkspaceFixture());
    return true;
  } catch (error) {
    if (!(error instanceof EngineCapabilityMissing)) throw error;
    t.skip(`this AppBundle predates the source exports: ${error.message}`);
    return false;
  }
}

// The tictactoe fragment composed over the standard basis, from the official tree's sources mounted the way the
// studio mounts them, with a small `.puck` root beside them. The composed document carries Int64.Min/MaxValue
// sentinels (state row min/max) that JavaScript's own JSON.parse/stringify round trip cannot preserve exactly, so
// `compile()` gets `composeSource()`'s raw, never-JS-round-tripped text.
async function composeTicTacToeText(engine) {
  await engine.mountSources({ ...officialSources(), [TICTACTOE_ROOT.path]: TICTACTOE_ROOT.text });
  const result = await engine.composeSource(TICTACTOE_ROOT.path);
  assert.equal(result.ok, true, JSON.stringify(result.diagnostics));
  assert.equal(typeof result.composed, 'string');
  return result.composed;
}

function overBudgetDraftText() {
  const effects = Array.from({ length: 256 }, () => ({ $type: 'addState', state: 'count', value: 1 }));
  return JSON.stringify({
    schema: 'puck.world.definition.v1',
    state: { world: [
      { name: 'items', kind: 'Int', capacity: 4096 },
      { name: 'count', kind: 'Int', value: 0 },
    ] },
    rules: [
      { name: 'many-a', forEach: 'items', effects },
      { name: 'many-b', forEach: 'items', effects },
    ],
  });
}

if (!fs.existsSync(mainMjs)) {
  test(`native (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release')`, { skip: true }, () => {});
} else {
  test('costs reads the shared report through the real WASM export', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    const compiled = await engine.compile('{"schema":"puck.world.definition.v1"}');
    assert.equal(compiled.ok, true, JSON.stringify(compiled.errors));
    try {
      const report = await engine.costs(compiled.handle);
      assert.equal(report.modelId, 'puck.cost.portable-model.v1');
      assert.match(report.evidenceDigest, /^[a-f0-9]{64}$/i);
      assert.equal(typeof report.stepAllowanceCycles, 'bigint');
      assert.equal(report.totalBound.kind, 'Unmodeled');
      assert.equal(report.totalBound.cycles, null);
      assert.equal(report.admitted, false);
    } finally {
      await engine.release(compiled.handle);
      await engine.dispose();
    }
  });

  test('analyzeCosts reports a structurally valid over-budget draft while compile refuses it', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const draft = overBudgetDraftText();
      const analysis = await engine.analyzeCosts(draft);
      assert.equal(analysis.ok, true, JSON.stringify(analysis.errors));
      assert.equal(analysis.validated, false);
      assert.ok(analysis.validationErrors.some((error) => error.message.includes('the maximum of')));
      assert.equal(analysis.report.modelId, 'puck.cost.portable-model.v1');
      assert.equal(typeof analysis.report.resources.journalAllowanceBytes, 'bigint');
      assert.ok(BigInt(analysis.report.heuristicWorkUnitsPerTick) > 4000000n);

      const compiled = await engine.compile(draft);
      assert.equal(compiled.ok, false);
      assert.ok(compiled.errors.some((error) => error.message.includes('the maximum of')));
    } finally {
      await engine.dispose();
    }
  });
  test('bootEngineFromLocalBundle: version() decodes to a plain object, no bigints yet', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const version = await engine.version();
      assert.equal(typeof version.schemaVersion, 'string');
      assert.equal(version.engine, 'Puck.World.Browser');
      assert.equal(typeof version.commit, 'string');
    } finally {
      await engine.dispose();
    }
  });

  test('createEngineHost({mode:"inline"}) is engineHost.ts\'s own dispatcher for the same boot path', async () => {
    const { pathToFileURL } = require('node:url');
    const engine = await createEngineHost({ mode: 'inline', engineEntryUrl: pathToFileURL(mainMjs).href });
    try {
      const version = await engine.version();
      assert.equal(version.engine, 'Puck.World.Browser');
    } finally {
      await engine.dispose();
    }
  });

  test('parse() decodes both ParseResult arms through the facade', async (t) => {
    if (!needsTree(t)) return;
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const bad = await engine.parse(JSON.stringify({ schema: 'not.a.real.schema' }));
      assert.equal(bad.ok, false);
      assert.ok(Array.isArray(bad.errors));
      assert.ok(bad.errors.some((e) => e.message.includes('not.a.real.schema')));
      assert.ok(Array.isArray(bad.deferred));

      const hostJson = officialDocument('standard');
      const good = await engine.parse(hostJson);
      assert.equal(good.ok, true, JSON.stringify(good));
      assert.equal(typeof good.document, 'object');
    } finally {
      await engine.dispose();
    }
  });

  test('cells() decodes EngineCell[] with plain numbers, not bigints (ordinals are small)', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const topology = { $type: 'grid', name: 'g', width: 2, depth: 2, cellSize: 1, band: 0.3, origin: [0, 0, 0] };
      const result = await engine.cells(JSON.stringify(topology));
      assert.equal(result.ok, true);
      assert.equal(result.cells.length, 4);
      assert.deepEqual(result.cells[0], { ordinal: 0, key: '0', x: 0.5, y: 0, z: 0.5 });
    } finally {
      await engine.dispose();
    }
  });

  test('compile()/rows()/writeRow()/judge()/stateHash() round-trip bigints through the facade', async (t) => {
    if (!needsTree(t)) return;
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      if (!(await needsSourceExports(t, engine))) return;
      const composedText = await composeTicTacToeText(engine);
      const compiled = await engine.compile(composedText);
      assert.equal(compiled.ok, true, JSON.stringify(compiled));
      const handle = compiled.handle;

      const rows = await engine.rows(handle);
      assert.ok(rows.length > 0);
      const row = rows.find((r) => r.kind === 'Int' && !r.keyed);
      assert.ok(row, 'no scalar Int row in the composed document');
      for (const cell of row.cells) {
        assert.equal(cell.value.kind, 'Int', `RowInfo cell for '${row.name}' must carry its row's own kind`);
        assert.equal(typeof cell.value.value, 'bigint', `RowInfo cell value for '${row.name}' must be a bigint, not ${typeof cell.value.value}`);
      }

      // 1 (not an arbitrary larger value) - matches the row's own authored envelope, exactly as
      // engine-wasm.test.cjs's own "tictactoe-write-then-judge" fixture writes.
      const written = await engine.writeRow(handle, row.name, undefined, 1n, 'set');
      assert.equal(written.ok, true, written.error);
      const read = await engine.readRow(handle, row.name);
      assert.equal(read.found, true);
      assert.equal(read.value.kind, 'Int');
      assert.equal(read.value.value, 1n);
      assert.equal(typeof read.value.value, 'bigint');

      const judged = await engine.judge(handle, 1n);
      assert.equal(judged.ok, true, judged.error);
      assert.ok(Array.isArray(judged.trace.rules));
      for (const write of judged.trace.writes) {
        assert.equal(typeof write.old, 'bigint');
        assert.equal(typeof write.new, 'bigint');
      }

      const hash = await engine.stateHash(handle);
      assert.equal(typeof hash, 'string');
      assert.ok(hash.length > 0);

      await engine.release(handle);
    } finally {
      await engine.dispose();
    }
  });

  test('evaluate() and boardMask() decode their bigint arms', async (t) => {
    if (!needsTree(t)) return;
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      if (!(await needsSourceExports(t, engine))) return;
      const composedText = await composeTicTacToeText(engine);
      const compiled = await engine.compile(composedText);
      assert.equal(compiled.ok, true, JSON.stringify(compiled));
      const handle = compiled.handle;

      const evaluated = await engine.evaluate(handle, '1 + 2', 'Int', 0n);
      assert.equal(evaluated.ok, true, evaluated.error);
      assert.equal(evaluated.value, 3n);
      assert.equal(typeof evaluated.value, 'bigint');

      // boardMask() only accepts a board-shaped row (at most 64 cells) - not every keyed row
      // qualifies (tictactoe.world.json also carries larger keyed rows, e.g. 'transforms'), so try
      // each keyed row until one is accepted rather than assuming the first is board-shaped.
      const rows = await engine.rows(handle);
      let boardMaskChecked = false;
      for (const candidate of rows.filter((r) => r.keyed)) {
        const mask = await engine.boardMask(handle, candidate.name);
        if (mask.ok) {
          assert.equal(typeof mask.mask, 'bigint');
          boardMaskChecked = true;
          break;
        }
      }
      assert.ok(boardMaskChecked, 'no keyed row in the composed document is board-shaped (<=64 cells)');

      await engine.release(handle);
    } finally {
      await engine.dispose();
    }
  });

  test('mountSources()/compileSource()/composeSource() decode a source workspace\'s answers', async (t) => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      if (!(await needsSourceExports(t, engine))) return;
      const compiled = await engine.compileSource('counter.puck');
      assert.equal(compiled.ok, true, JSON.stringify(compiled.diagnostics));
      assert.equal(JSON.parse(compiled.document).documentId, 'counter');
      assert.ok(Object.values(compiled.sourceMap).some((span) => span.path === 'counter.puck' && span.line > 0), 'compiled values map back to their source');
      assert.deepEqual(compiled.worlds, []);

      const composed = await engine.composeSource('counter.puck');
      assert.equal(composed.ok, true, JSON.stringify(composed.diagnostics));
      assert.deepEqual(composed.diagnostics.filter((diagnostic) => diagnostic.severity === 'error'), []);
      assert.equal(JSON.parse(composed.composed).documentId, 'counter');

      // A container never takes a colon (PUCK040), reported where it was written, 1-based.
      await engine.writeSource('counter.puck', 'schema: "puck.world.definition.v1"\nstate: {\n}\n');
      const refused = await engine.compileSource('counter.puck');
      assert.equal(refused.ok, false);
      assert.equal(refused.document, null);
      assert.ok(refused.diagnostics.some((diagnostic) => diagnostic.code === 'PUCK040' && diagnostic.path === 'counter.puck' && diagnostic.line === 2 && diagnostic.column === 1));

      const uncomposed = await engine.composeSource('counter.puck');
      assert.equal(uncomposed.ok, false);
      assert.equal(uncomposed.composed, null, 'nothing composed');
      assert.ok(uncomposed.diagnostics.some((diagnostic) => diagnostic.severity === 'error' && diagnostic.path === 'counter.puck'), JSON.stringify(uncomposed.diagnostics));
      await assert.rejects(engine.writeSource('../escape.puck', ''), 'a path outside the workspace is refused');
    } finally {
      await engine.dispose();
    }
  });

  test('every rule of a compiled source maps to its own rule line, so a rule-trace link lands on the rule itself', async (t) => {
    if (!needsTree(t)) return;
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      if (!(await needsSourceExports(t, engine))) return;
      const sources = officialSources();
      await engine.mountSources(sources);
      const source = 'games/tictactoe.puck';
      const compiled = await engine.compileSource(source);
      assert.equal(compiled.ok, true, JSON.stringify(compiled.diagnostics));
      const document = parseDocumentText(compiled.document);
      const lines = sources[source].split('\n');
      assert.ok(document.rules.length > 0);
      for (const rule of document.rules) {
        assert.ok(compiled.sourceMap[pathToPointer(rulePath(document, rule.name))], `rule '${rule.name}' has its own pointer`);
        const span = ruleSpan(document, compiled.sourceMap, rule.name);
        assert.equal(span.path, source);
        assert.ok(lines[span.line - 1].includes(`rule "${rule.name}"`), `rule '${rule.name}' links to line ${span.line}: ${lines[span.line - 1]}`);
      }
    } finally {
      await engine.dispose();
    }
  });

  test('lsp()/lspIdle() answer at once and diagnose only when idle, for the version they were given', async (t) => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      if (!(await needsSourceExports(t, engine))) return;
      const [initialized] = await engine.lsp(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { capabilities: {} } }));
      assert.equal(initialized.id, 1);
      assert.ok(initialized.result.capabilities.semanticTokensProvider.legend.tokenTypes.length > 0, 'the token legend arrives with initialize');
      const uri = sourceUri('counter.puck');
      const opened = await engine.lsp(JSON.stringify({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri, languageId: 'puck', version: 4, text: readWorkspaceFixture()['counter.puck'] } } }));
      assert.equal(opened.filter((message) => message.method === 'textDocument/publishDiagnostics').length, 0, 'didOpen never diagnoses inline');
      const published = [];
      for (let units = 0; units < 16; units++) {
        const idle = await engine.lspIdle();
        published.push(...idle.messages.filter((message) => message.method === 'textDocument/publishDiagnostics'));
        if (!idle.pending) break;
      }
      assert.ok(published.some((message) => message.params.uri === uri && message.params.version === 4));
      assert.equal((await engine.lspIdle()).ran, false, 'nothing is pending once the units ran');
    } finally {
      await engine.dispose();
    }
  });

  test('dispose() is safe to call and the engine handle is no longer needed afterward', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    const version = await engine.version();
    assert.equal(version.engine, 'Puck.World.Browser');
    await engine.dispose();
    // inlineHost's dispose has no real teardown for an in-process Mono runtime (see its own
    // remarks) - calling it is safe, and does not itself throw.
    await engine.dispose();
  });
}
