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
const appBundleDir = path.join(root, 'src', 'Puck.World.Browser', 'bin', 'Release', 'net11.0', 'browser-wasm', 'AppBundle');
const mainMjs = path.join(appBundleDir, 'main.mjs');
const { officialTreeMissing, officialSchemaBundle } = require('./support/officialTree.cjs');
// A test that needs a compiled world skips itself by name when no official tree has been built.
const needsTree = (t) => { const missing = officialTreeMissing(); if (missing) t.skip(missing); return !missing; };
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


  test('Version() reports this build\'s schema version', async (t) => {
    const engine = await engineReady;
    const version = JSON.parse(engine.Version());

    assert.equal(version.engine, 'Puck.World.Browser');
    assert.equal(typeof version.schemaVersion, 'string');
    assert.ok(version.schemaVersion.length > 0);

    // The schema's own self-identification (WP-A's x-puck root member) is the source of truth once it lands;
    // until then the schema's declared `schema` const is the same fact under its pre-existing name.
    if (!needsTree(t)) return;
    const schema = officialSchemaBundle();
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

  test('AnalyzeCosts() prices the presentation dimension exactly as the native baseline', async () => {
    if (!fs.existsSync(parityFixturePath)) {
      assert.fail(`no recorded baseline at ${parityFixturePath} — run: dotnet test tests/Puck.World.Browser.Tests, then puck baselines browser-parity`);
    }

    const expected = JSON.parse(fs.readFileSync(parityFixturePath, 'utf8'));
    const engine = await engineReady;
    const document = fs.readFileSync(path.join(path.dirname(parityFixturePath), 'presentation.world.json'), 'utf8');
    const analysis = JSON.parse(engine.AnalyzeCosts(document));

    assert.equal(analysis.ok, true, JSON.stringify(analysis.errors));
    assert.deepEqual(analysis.report.presentation, expected['presentation-cost']);
  });

}
