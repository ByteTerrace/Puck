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

  test('ComposeTree() composes the real island and defers its unregistered machine engines', async () => {
    const engine = await engineReady;
    const documents = {};
    for (const file of fs.readdirSync(worldsDir, { recursive: true })) {
      if (!file.endsWith('.json')) continue;
      documents[file.split(path.sep).join('/')] = fs.readFileSync(path.join(worldsDir, file), 'utf8');
    }

    const result = JSON.parse(engine.ComposeTree('puck.world.json', JSON.stringify(documents), '', ''));

    assert.equal(result.ok, true, JSON.stringify(result.errors));
    assert.ok(result.composed);
    assert.ok(result.document);
    assert.ok(result.deferred.some(m => m.includes("screen-machine engine 'gaming-brick' registration deferred")));
  });

  test('Judge() over the composed island runs ticks 1-3 and reports hostFacts for a world-scoped read', async () => {
    const engine = await engineReady;
    const documents = {};
    for (const file of fs.readdirSync(worldsDir, { recursive: true })) {
      if (!file.endsWith('.json')) continue;
      documents[file.split(path.sep).join('/')] = fs.readFileSync(path.join(worldsDir, file), 'utf8');
    }

    const composed = JSON.parse(engine.ComposeTree('puck.world.json', JSON.stringify(documents), '', ''));
    assert.equal(composed.ok, true, JSON.stringify(composed.errors));

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

  // Duplicates the fragment's first rule object by TEXT surgery, never JSON.parse/stringify of the whole document —
  // tictactoe.world.json carries Int64.Min/MaxValue sentinels (state row min/max) JavaScript's own JSON round trip
  // cannot preserve exactly (see documentValidation.ts's own remarks); splicing the raw text leaves every other
  // byte, including those sentinels, untouched.
  function withADuplicatedFirstRule(fragmentJson) {
    const arrayOpen = fragmentJson.indexOf('[', fragmentJson.indexOf('"rules"'));
    const rulesStart = fragmentJson.indexOf('{', arrayOpen);
    let depth = 0, ruleEnd = rulesStart;
    for (let i = rulesStart; i < fragmentJson.length; i++) {
      if (fragmentJson[i] === '{') depth++;
      else if (fragmentJson[i] === '}' && --depth === 0) { ruleEnd = i + 1; break; }
    }
    const firstRule = fragmentJson.slice(rulesStart, ruleEnd);

    return fragmentJson.slice(0, rulesStart) + firstRule + ',' + fragmentJson.slice(rulesStart);
  }

  test('ComposeTree() with an edited document reports its own diagnostic, not another document\'s', async () => {
    const engine = await engineReady;
    const basisJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
    const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
    const rootJson = JSON.stringify({ basis: 'standard.basis.json', imports: [{ document: 'games/tictactoe.world.json' }] });
    const documents = { 'test-root.json': rootJson, 'standard.basis.json': basisJson, 'games/tictactoe.world.json': fragmentJson };

    const result = JSON.parse(engine.ComposeTree(
      'test-root.json',
      JSON.stringify(documents),
      'games/tictactoe.world.json',
      withADuplicatedFirstRule(fragmentJson),
    ));

    assert.equal(result.ok, false);
    assert.ok(result.errors.some(e => e.message.includes('ttt-place-mark') && e.message.includes("duplicates an earlier rule's name")));
    assert.ok(!result.errors.some(e => e.message.includes('test-root.json:')));
  });
}
