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
const {
  officialTreeMissing, officialDocument, officialSchemaBundle, officialSources, officialIslandRootSource, TICTACTOE_ROOT,
} = require('./support/officialTree.cjs');
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

  test('ParseFragment() composes the tictactoe fragment under the standard basis', async (t) => {
    if (!needsTree(t)) return;
    const engine = await engineReady;
    const hostJson = officialDocument('standard');
    const fragmentJson = officialDocument('games/tictactoe');
    const result = JSON.parse(engine.ParseFragment(fragmentJson, hostJson, 'a'));

    assert.equal(result.ok, true, JSON.stringify(result.errors));
    assert.ok(result.document);
  });

  test('Compile()/Judge()/StateHash() match the native parity baseline', async (t) => {
    if (!needsTree(t)) return;
    if (!fs.existsSync(parityFixturePath)) {
      // The record run (PUCK_BROWSER_PARITY_RECORD=1 dotnet test tests/Puck.World.Browser.Tests) is the one
      // producer of this file; without it there is no baseline to compare the wasm run against.
      assert.fail(`no recorded baseline at ${parityFixturePath} — run: PUCK_BROWSER_PARITY_RECORD=1 dotnet test tests/Puck.World.Browser.Tests`);
    }

    const expected = JSON.parse(fs.readFileSync(parityFixturePath, 'utf8'));
    const engine = await engineReady;
    const hostJson = officialDocument('standard');
    const fragmentJson = officialDocument('games/tictactoe');
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

  // The source-workspace tests mount the official tree's sources[] the way the studio does, so they need the source
  // exports.
  async function needsSources(t) {
    if (!needsTree(t)) return null;
    const engine = await engineReady;
    if (typeof engine.MountSources !== 'function') {
      t.skip('this AppBundle predates the source exports (no MountSources)');
      return null;
    }
    return engine;
  }
  const mount = (engine, files) => {
    const mounted = JSON.parse(engine.MountSources(JSON.stringify(files)));
    assert.equal(mounted.ok, true, mounted.error);
  };
  async function needsIsland(t) {
    const engine = await needsSources(t);
    if (engine) mount(engine, officialSources());
    return engine;
  }

  test('ComposeSource() composes the real island, and reports the machine checks it defers as information', async (t) => {
    const engine = await needsIsland(t);
    if (!engine) return;

    const result = JSON.parse(engine.ComposeSource(officialIslandRootSource()));
    assert.equal(result.ok, true, JSON.stringify(result.diagnostics));
    assert.deepEqual(result.diagnostics.filter(d => d.severity === 'error'), []);
    const deferrals = result.diagnostics.filter(d => d.severity === 'information' && d.message.includes('no machine catalog was supplied'));
    assert.ok(deferrals.length > 0, 'the browser has no machine catalog, so it defers those checks');
    assert.ok(deferrals.every(d => d.line === 0), 'a composed-world finding is about the whole document');
    const parsed = JSON.parse(engine.Parse(result.composed));
    assert.equal(parsed.ok, true, JSON.stringify(parsed.errors));
    assert.ok(parsed.deferred.some(m => m.includes("no machine catalog was supplied for 'gaming-brick'")));
  });

  test('Judge() over the composed island runs ticks 1-3 and reports hostFacts for a world-scoped read', async (t) => {
    const engine = await needsIsland(t);
    if (!engine) return;

    const composed = JSON.parse(engine.ComposeSource(officialIslandRootSource()));
    assert.equal(composed.ok, true, JSON.stringify(composed.diagnostics));

    const compiled = JSON.parse(engine.Compile(composed.composed));
    assert.equal(compiled.ok, true, JSON.stringify(compiled.errors));

    const hostFacts = [];
    for (const tick of ['1', '2', '3']) {
      const judged = JSON.parse(engine.Judge(compiled.handle, tick));
      assert.equal(judged.ok, true, judged.error);
      hostFacts.push(...(judged.trace.hostFacts || []));
    }
    engine.Release(compiled.handle);

    assert.ok(hostFacts.length > 0, 'the composed island\'s dive/kart/jump rules must read at least one world-scoped operand');
    assert.ok(hostFacts.some(f => (f.operand.includes('Physics') || f.operand.includes('Body'))));
  });

  // Duplicates the fragment's first rule block by text surgery on its .puck source.
  function withADuplicatedFirstRule(source) {
    const start = source.indexOf('rule "');
    const end = source.indexOf('\n}\n', start) + 3;
    return source.slice(0, end) + '\n' + source.slice(start);
  }

  test('CompileSource() of a root reports an edited import\'s duplicate rule by name', async (t) => {
    const engine = await needsSources(t);
    if (!engine) return;
    const sources = officialSources();
    mount(engine, { ...sources, [TICTACTOE_ROOT.path]: TICTACTOE_ROOT.text });
    const clean = JSON.parse(engine.CompileSource(TICTACTOE_ROOT.path));
    assert.deepEqual(clean.diagnostics.filter(d => d.severity === 'error'), [], 'the unedited composition is clean');

    const written = JSON.parse(engine.WriteSource('games/tictactoe.puck', withADuplicatedFirstRule(sources['games/tictactoe.puck'])));
    assert.equal(written.ok, true, written.error);
    const result = JSON.parse(engine.CompileSource(TICTACTOE_ROOT.path));

    assert.equal(result.ok, false);
    assert.ok(result.diagnostics.some(d => d.severity === 'error' && d.message.includes('ttt-place-mark') && d.message.includes("duplicates an earlier rule's name")),
      JSON.stringify(result.diagnostics));
  });
}
