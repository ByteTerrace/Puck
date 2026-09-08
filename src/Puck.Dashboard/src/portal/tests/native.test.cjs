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
const ts = require('typescript');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

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
const worldsDir = path.join(root, 'src', 'Puck.World', 'Assets', 'worlds');

// tictactoe.world.json carries Int64.Min/MaxValue sentinels (state row min/max) that JavaScript's
// own JSON.parse/stringify round trip cannot preserve exactly (see engine-wasm.test.cjs's own
// remarks). `parseFragment()`'s facade already parses its `document` into a plain JS value, so
// re-stringifying THAT for compile() loses precision; `composeTree()`'s own `composed` field is
// the raw, never-JS-round-tripped text instead, so `compile()` gets the exact original bytes.
async function composeTicTacToeText(engine) {
  const basisJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
  const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
  const rootJson = JSON.stringify({ basis: 'standard.basis.json', imports: [{ document: 'games/tictactoe.world.json' }] });
  const documents = { 'test-root.json': rootJson, 'standard.basis.json': basisJson, 'games/tictactoe.world.json': fragmentJson };
  const result = await engine.composeTree('test-root.json', documents);
  assert.equal(result.ok, true, JSON.stringify(result.errors));
  assert.equal(typeof result.composed, 'string');
  return result.composed;
}

if (!fs.existsSync(mainMjs)) {
  test(`native (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release -r browser-wasm')`, { skip: true }, () => {});
} else {
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

  test('parse() decodes both ParseResult arms through the facade', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const bad = await engine.parse(JSON.stringify({ schema: 'not.a.real.schema' }));
      assert.equal(bad.ok, false);
      assert.ok(Array.isArray(bad.errors));
      assert.ok(bad.errors.some((e) => e.message.includes('not.a.real.schema')));
      assert.ok(Array.isArray(bad.deferred));

      const hostJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
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

  test('compile()/rows()/writeRow()/judge()/stateHash() round-trip bigints through the facade', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const composedText = await composeTicTacToeText(engine);
      const compiled = await engine.compile(composedText);
      assert.equal(compiled.ok, true, JSON.stringify(compiled));
      const handle = compiled.handle;

      const rows = await engine.rows(handle);
      assert.ok(rows.length > 0);
      const row = rows.find((r) => r.kind === 'Int' && !r.keyed);
      assert.ok(row, 'no scalar Int row in the composed document');
      for (const cell of row.cells) {
        assert.equal(typeof cell.value, 'bigint', `RowInfo cell value for '${row.name}' must be a bigint, not ${typeof cell.value}`);
      }

      // 1 (not an arbitrary larger value) - matches the row's own authored envelope, exactly as
      // engine-wasm.test.cjs's own "tictactoe-write-then-judge" fixture writes.
      const written = await engine.writeRow(handle, row.name, undefined, 1n, 'set');
      assert.equal(written.ok, true, written.error);
      const read = await engine.readRow(handle, row.name);
      assert.equal(read.value, 1n);
      assert.equal(typeof read.value, 'bigint');

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

  test('evaluate() and boardMask() decode their bigint arms', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
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
