// Loads the Puck.World.Browser AppBundle's own dotnet.js under Node and drives the real [JSExport] surface —
// the same bytes a browser tab would fetch. Never a synthetic stand-in: every call here crosses into the actual
// wasm-compiled engine, so a marshalling regression (a decimal-string long, a JSON envelope shape) fails here
// before it ever reaches the studio.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');

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
const parityFixturePath = path.join(root, 'tests', 'Puck.World.Browser.Tests', 'Fixtures', 'browser-parity', 'expected.json');

if (!fs.existsSync(mainMjs)) {
  // A named skip, never a silent pass: `dotnet publish src/Puck.World.Browser -c Release -r browser-wasm`
  // generates the AppBundle these tests load; without it there is nothing to load and nothing to prove.
  test(`engine-wasm (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release -r browser-wasm')`, { skip: true }, () => {});
} else {
  const engineReady = (async () => {
    const { createEngine } = await import(pathToFileURL(mainMjs).href);
    return createEngine();
  })();

  test('Version() reports this build\'s schema version', async () => {
    const engine = await engineReady;
    const version = JSON.parse(engine.Version());

    assert.equal(version.engine, 'Puck.World.Browser');
    assert.equal(typeof version.schemaVersion, 'string');
    assert.ok(version.schemaVersion.length > 0);

    // The schema's own self-identification (WP-A's x-puck root member) is the source of truth once it lands;
    // until then the schema's declared `schema` const is the same fact under its pre-existing name.
    const schemaPath = path.join(worldsDir, 'puck.world.def.v1.schema.json');
    const schema = JSON.parse(fs.readFileSync(schemaPath, 'utf8'));
    const expected = (schema['x-puck'] && schema['x-puck'].schemaVersion)
      || (schema.properties && schema.properties.schema && schema.properties.schema.const)
      || version.schemaVersion;

    assert.equal(version.schemaVersion, expected);
  });

  test('Parse() refuses a document naming the wrong schema, by name', async () => {
    const engine = await engineReady;
    const result = JSON.parse(engine.Parse(JSON.stringify({ schema: 'not.a.real.schema' })));

    assert.equal(result.ok, false);
    assert.ok(result.errors.some(e => e.message.includes('not.a.real.schema')));
  });

  test('Cells() compiles a standalone grid topology into ordinal geometry', async () => {
    const engine = await engineReady;
    const topology = { $type: 'grid', name: 'g', width: 2, depth: 2, cellSize: 1, band: 0.3, origin: [0, 0, 0] };
    const result = JSON.parse(engine.Cells(JSON.stringify(topology)));

    assert.equal(result.ok, true);
    assert.equal(result.cells.length, 4);
    assert.deepEqual(result.cells[0], { ordinal: 0, key: '0', x: 0.5, y: 0, z: 0.5 });
  });

  test('ParseFragment() composes the tictactoe fragment under standard.basis.json', async () => {
    const engine = await engineReady;
    const hostJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
    const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
    const result = JSON.parse(engine.ParseFragment(fragmentJson, hostJson, 'a'));

    assert.equal(result.ok, true, JSON.stringify(result.errors));
    assert.ok(result.document);
  });

  test('Compile()/Judge()/StateHash() match the native parity baseline', async () => {
    if (!fs.existsSync(parityFixturePath)) {
      // The record run (PUCK_BROWSER_PARITY_RECORD=1 dotnet test tests/Puck.World.Browser.Tests) is the one
      // producer of this file; without it there is no baseline to compare the wasm run against.
      assert.fail(`no recorded baseline at ${parityFixturePath} — run: PUCK_BROWSER_PARITY_RECORD=1 dotnet test tests/Puck.World.Browser.Tests`);
    }

    const expected = JSON.parse(fs.readFileSync(parityFixturePath, 'utf8'));
    const engine = await engineReady;
    const hostJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
    const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
    const composed = JSON.parse(engine.ParseFragment(fragmentJson, hostJson, 'a'));

    assert.equal(composed.ok, true, JSON.stringify(composed.errors));

    async function freshHandle() {
      const compiled = JSON.parse(engine.Compile(composed.document));
      assert.equal(compiled.ok, true, JSON.stringify(compiled.errors));
      return compiled.handle;
    }
    function firstScalarIntRow(handle) {
      const rows = JSON.parse(engine.Rows(handle));
      assert.equal(rows.ok, true);
      const row = rows.rows.find(r => (r.kind === 'Int') && !r.keyed);
      assert.ok(row, 'no scalar Int row in the composed document');
      return row.name;
    }

    // "tictactoe-write-then-judge": write the same row the native fixture writes, then judge tick 1.
    {
      const handle = await freshHandle();
      const row = firstScalarIntRow(handle);
      const written = JSON.parse(engine.WriteRow(handle, row, '$value', '1', 'set'));
      assert.equal(written.ok, true, written.error);
      engine.Judge(handle, '1');
      const hash = JSON.parse(engine.StateHash(handle));
      assert.equal(hash.ok, true);
      assert.equal(hash.hash, expected['tictactoe-write-then-judge']);
      engine.Release(handle);
    }

    // "tictactoe-judge-twice": no write, judge tick 1 then tick 2.
    {
      const handle = await freshHandle();
      engine.Judge(handle, '1');
      engine.Judge(handle, '2');
      const hash = JSON.parse(engine.StateHash(handle));
      assert.equal(hash.ok, true);
      assert.equal(hash.hash, expected['tictactoe-judge-twice']);
      engine.Release(handle);
    }
  });
}
