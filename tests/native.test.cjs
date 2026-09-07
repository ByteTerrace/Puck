// Exercises the TypeScript facade (src/Puck.Dashboard/src/portal/src/native/) end to end against the real
// Puck.World.Browser AppBundle, in 'inline' mode — Node has no Worker/DOM, so 'worker' mode has no host here.
// Every call crosses through wrapRawExports' actual wire decoding: a marshalling regression (a decimal-string long
// that never becomes a bigint, a JSON envelope shape the facade misreads) fails here, not only at the raw-export
// layer engine-wasm.test.cjs already covers.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { register } = require('node:module');

// The portal's TypeScript sources author extensionless relative imports (Vite's own "bundler" resolution); Node's
// native TypeScript support requires an explicit extension on a relative specifier. native.loader.mjs bridges that
// one gap so the real, unmodified sources load as-is — see its own remarks.
register(pathToFileURL(path.join(__dirname, 'native.loader.mjs')).href, pathToFileURL(__filename).href);

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
const mainMjsPath = path.join(appBundleDir, 'main.mjs');
const mainMjsUrl = pathToFileURL(mainMjsPath).href;
const worldsDir = path.join(root, 'src', 'Puck.World', 'Assets', 'worlds');
const nativeDir = path.join(root, 'src', 'Puck.Dashboard', 'src', 'portal', 'src', 'native');

if (!fs.existsSync(mainMjsPath)) {
  test(`native (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release -r browser-wasm')`, { skip: true }, () => {});
} else {
  const engineReady = (async () => {
    const { createEngineHost } = await import(pathToFileURL(path.join(nativeDir, 'engineHost.ts')).href);

    return createEngineHost({ mode: 'inline', engineEntryUrl: mainMjsUrl });
  })();

  // Every *.json file under the worlds directory, keyed by its forward-slash path relative to it — the same
  // worlds-relative name shape composeTree's own documents map expects.
  function worldsDirectoryDocuments() {
    const documents = {};
    for (const file of fs.readdirSync(worldsDir, { recursive: true })) {
      if (!file.endsWith('.json')) continue;
      documents[file.split(path.sep).join('/')] = fs.readFileSync(path.join(worldsDir, file), 'utf8');
    }
    return documents;
  }

  test('version() reports this build\'s schema version', async () => {
    const engine = await engineReady;
    const version = await engine.version();

    assert.equal(version.engine, 'Puck.World.Browser');
    assert.equal(typeof version.schemaVersion, 'string');
    assert.ok(version.schemaVersion.length > 0);
  });

  test('parse() reports an ok result and a refuse result, both carrying string diagnostic paths', async () => {
    const engine = await engineReady;

    const refused = await engine.parse(JSON.stringify({ schema: 'not.a.real.schema' }));
    assert.equal(refused.ok, false);
    assert.ok(refused.errors.length > 0);
    for (const error of refused.errors) assert.equal(typeof error.path, 'string');
    assert.ok(refused.errors.some(e => e.message.includes('not.a.real.schema')));
    assert.deepEqual(refused.deferred, []);

    const hostJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
    const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
    const admitted = await engine.parseFragment(fragmentJson, hostJson, 'a');
    assert.equal(admitted.ok, true, JSON.stringify(admitted.errors));
    assert.equal(typeof admitted.document, 'object');
  });

  test('composeTree() composes the real island from the worlds directory with the arcade deferrals', async () => {
    const engine = await engineReady;
    const result = await engine.composeTree('puck.world.json', worldsDirectoryDocuments());

    assert.equal(result.ok, true, JSON.stringify(result.errors));
    assert.ok(result.composed);
    assert.ok(result.document);
    assert.ok(result.deferred.some(m => m.includes("screen-machine engine 'gaming-brick' registration deferred")));
    assert.ok(result.deferred.some(m => m.includes("screen-machine engine 'advanced-gaming-brick' registration deferred")));
  });

  test('compile()/judge()/writeRow()/readRow()/stateHash() round-trip on tictactoe composed under the basis', async () => {
    const engine = await engineReady;
    const documents = {
      'test-root.json': JSON.stringify({ basis: 'standard.basis.json', imports: [{ document: 'games/tictactoe.world.json' }] }),
      'standard.basis.json': fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8'),
      'games/tictactoe.world.json': fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8'),
    };
    const composed = await engine.composeTree('test-root.json', documents);
    assert.equal(composed.ok, true, JSON.stringify(composed.errors));

    const compiled = await engine.compile(composed.composed);
    assert.equal(compiled.ok, true, JSON.stringify(compiled.errors ?? compiled));
    const handle = compiled.handle;

    try {
      const rows = await engine.rows(handle);
      // tttMoveCount: kind Int, unkeyed, declares 0..64 — wide enough that this write never brushes its envelope.
      const scalarRow = rows.find(r => (r.name === 'tttMoveCount') && (r.kind === 'Int') && !r.keyed);
      assert.ok(scalarRow, 'tttMoveCount is not a scalar Int row in the composed document');

      const written = await engine.writeRow(handle, scalarRow.name, undefined, 3n, 'set');
      assert.equal(written.ok, true, written.error);

      const judged = await engine.judge(handle, 1n);
      assert.equal(judged.ok, true, judged.error);
      assert.ok(Array.isArray(judged.trace.rules));
      assert.ok(Array.isArray(judged.trace.writes));
      for (const write of judged.trace.writes) {
        assert.equal(typeof write.old, 'bigint');
        assert.equal(typeof write.new, 'bigint');
      }

      const read = await engine.readRow(handle, scalarRow.name);
      assert.equal(typeof read.value, 'bigint');
      assert.equal(read.found, true);

      const hash = await engine.stateHash(handle);
      assert.equal(typeof hash, 'string');
      assert.ok(hash.length > 0);
    } finally {
      await engine.release(handle);
    }
  });

  test('writeRow()/readRow() round-trip a bigint above Number.MAX_SAFE_INTEGER exactly', async () => {
    const engine = await engineReady;
    const documents = {
      'test-root.json': JSON.stringify({ basis: 'standard.basis.json', imports: [{ document: 'games/tictactoe.world.json' }] }),
      'standard.basis.json': fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8'),
      'games/tictactoe.world.json': fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8'),
    };
    const composed = await engine.composeTree('test-root.json', documents);
    assert.equal(composed.ok, true, JSON.stringify(composed.errors));
    const compiled = await engine.compile(composed.composed);
    assert.equal(compiled.ok, true);
    const handle = compiled.handle;

    try {
      // tttMaskX declares min/max at Int64's own bounds, the one row wide enough to carry a value this large.
      const large = (1n << 62n) + 1n; // 4611686018427387905 — far past 2^53, not representable as a JS number.
      const written = await engine.writeRow(handle, 'tttMaskX', undefined, large, 'set');
      assert.equal(written.ok, true, written.error);

      const read = await engine.readRow(handle, 'tttMaskX');
      assert.equal(read.value, large);
      assert.equal(typeof read.value, 'bigint');
    } finally {
      await engine.release(handle);
    }
  });

  test('cells() compiles ordinal geometry for a grid and a hex topology', async () => {
    const engine = await engineReady;

    const grid = await engine.cells(JSON.stringify({ $type: 'grid', name: 'g', width: 2, depth: 2, cellSize: 1, band: 0.3, origin: [0, 0, 0] }));
    assert.equal(grid.ok, true, grid.error);
    assert.equal(grid.cells.length, 4);
    assert.deepEqual(grid.cells[0], { ordinal: 0, key: '0', x: 0.5, y: 0, z: 0.5 });

    const hex = await engine.cells(JSON.stringify({ $type: 'hex', name: 'h', radius: 1, cellSize: 1, origin: [0, 0, 0] }));
    assert.equal(hex.ok, true, hex.error);
    assert.equal(hex.cells.length, 7);
    assert.equal(hex.cells[0].ordinal, 0);
  });
}
