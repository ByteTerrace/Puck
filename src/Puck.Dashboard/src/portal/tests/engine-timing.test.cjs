// Timing harness for Puck.World.Browser (Package P9 — wasm engine performance). Boots the real
// AppBundle through the same facade the studio calls (native/engineBoot.ts's own
// bootEngineFromLocalBundle — reused verbatim from tests/native.test.cjs), then times every call an editing loop
// makes over the shipped island — the built official tree's sources[] mounted the way the studio mounts them, the
// same workspace tests/engine-wasm.test.cjs's own 'ComposeSource() composes the real island' test reads — and prints
// a table.
//
// This is a MEASUREMENT harness, not a performance gate: it asserts only that each call succeeds
// (the same ok/error assertions tests/native.test.cjs already makes), never a timing threshold —
// wasm timing is too machine- and build-configuration-dependent to pin as a pass/fail. Run this file three times
// (`node --test tests/engine-timing.test.cjs`) and record the MEDIAN of each printed row in this
// project's README under "Performance" — see src/Puck.World.Browser/README.md.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');
const { performance } = require('node:perf_hooks');

require('./support/register.cjs');

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
const { EngineCapabilityMissing } = require('../src/native/engineTypes.ts');
const { officialTreeMissing, officialDocument, officialSources, officialIslandRootSource } = require('./support/officialTree.cjs');

if (!fs.existsSync(mainMjs)) {
  test(`engine-timing (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release -r browser-wasm')`, { skip: true }, () => {});
} else if (officialTreeMissing()) {
  test(`engine-timing (SKIPPED: ${officialTreeMissing()})`, { skip: true }, () => {});
} else {
  test('engine timing table: Version/Parse/MountSources/ComposeSource/Compile/Judge/StateHash/Cells over the shipped island', async (t) => {
    const rows = [];
    async function time(label, fn) {
      const start = performance.now();
      const result = await fn();
      rows.push({ label, ms: performance.now() - start });
      return result;
    }

    const bootStart = performance.now();
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    rows.push({ label: 'boot (createEngine -> ready [JSExport] surface)', ms: performance.now() - bootStart });

    const sizes = [];
    try {
      const version = await time('Version()', () => engine.version());
      assert.equal(version.engine, 'Puck.World.Browser');

      // Parse: the tictactoe fragment under the standard basis (ParseFragment) — the cheap, single-fragment call,
      // timed separately from the expensive full-island ComposeSource below.
      const basisJson = officialDocument('standard');
      const fragmentJson = officialDocument('games/tictactoe');
      const parsed = await time('Parse (tictactoe fragment under the standard basis)', () => engine.parseFragment(fragmentJson, basisJson, 'a'));
      assert.equal(parsed.ok, true, JSON.stringify(parsed));

      // ComposeSource: the real shipped island, composed from the official tree's sources[] mounted the way the studio
      // mounts them.
      const sources = officialSources();
      sizes.push({ label: 'sources payload (MountSources input, JSON.stringify(sources))', bytes: Buffer.byteLength(JSON.stringify(sources), 'utf8') });
      try {
        await time('MountSources (official sources[])', () => engine.mountSources(sources));
      } catch (error) {
        if (!(error instanceof EngineCapabilityMissing)) throw error;
        t.skip(`this AppBundle predates the source exports: ${error.message}`);
        return;
      }

      const composed = await time('ComposeSource (full island)', () => engine.composeSource(officialIslandRootSource()));
      assert.equal(composed.ok, true, JSON.stringify(composed.diagnostics));
      assert.ok(composed.composed, 'composeSource() must record the composed standalone document text');
      sizes.push({ label: 'composed island payload (ComposeSource output .composed)', bytes: Buffer.byteLength(composed.composed, 'utf8') });

      // Compile: the composed island's raw text — never JS-round-tripped (see native.test.cjs's own
      // remarks: a JSON.parse -> JSON.stringify round trip cannot preserve the tictactoe document's
      // Int64.Min/MaxValue sentinels exactly, and composeSource()'s own `.composed` field is the exact
      // original bytes for this reason).
      const compiled = await time('Compile (composed island)', () => engine.compile(composed.composed));
      assert.equal(compiled.ok, true, JSON.stringify(compiled));
      const handle = compiled.handle;

      // Judge: the composed island itself, not a tictactoe stand-in — BrowserRuleReader (Puck.World.Browser/
      // Engine/BrowserRuleReader.cs) answers the dive/kart/jump modules' world-scoped operand reads (e.g.
      // PhysicsQuiescentOperand) with this build's own honest hostless facts instead of throwing, and reports
      // each such read back on the trace's own hostFacts[] (see that type's own remarks and the project
      // README's "Verified scope boundary"). Three ticks, not one: an effect that reads a world-scoped
      // operand only inside its own fire (rather than a binding or gate conjunct) may not fire on tick 1
      // alone — BrowserHostlessIslandTests.cs judges the same three ticks for the same reason.
      let hostFactCount = 0;
      for (const tick of [1n, 2n, 3n]) {
        const judged = await time(`Judge (tick ${tick}, composed island)`, () => engine.judge(handle, tick));
        assert.equal(judged.ok, true, judged.error);
        hostFactCount += (judged.trace.hostFacts || []).length;
      }
      console.log(`hostFacts recorded across ticks 1-3 (composed island): ${hostFactCount}`);

      const hash = await time('StateHash (composed island)', () => engine.stateHash(handle));
      assert.equal(typeof hash, 'string');
      assert.ok(hash.length > 0);

      await engine.release(handle);

      // Cells: the composed island's 4x4x4 tic-tac-toe topology. Read as a plain JS object because
      // the lattice definition itself carries no Int64 sentinels.
      const tictactoeFragment = JSON.parse(fragmentJson);
      const cube = tictactoeFragment.state.lattices.find((l) => l.name === 'tttCube');
      assert.ok(cube, 'games/tictactoe must author a "tttCube" lattice');
      assert.equal(cube.width * cube.depth * cube.layers, 64);

      const cells = await time('Cells (tttCube, 4x4x4 box)', () => engine.cells(JSON.stringify(cube)));
      assert.equal(cells.ok, true, JSON.stringify(cells));
      assert.equal(cells.cells.length, 64);
    } finally {
      await engine.dispose();
    }

    const labelWidth = Math.max(...rows.map((r) => r.label.length), ...sizes.map((s) => s.label.length)) + 2;
    console.log('\n--- engine-timing (ms; run this file 3x, README records the MEDIAN of each row) ---');
    for (const r of rows) console.log(`${r.label.padEnd(labelWidth)} ${r.ms.toFixed(1)} ms`);
    console.log('--- payload sizes (marshalling cost, separate from engine work) ---');
    for (const s of sizes) console.log(`${s.label.padEnd(labelWidth)} ${s.bytes.toLocaleString('en-US')} bytes`);

    // Assertion contract: every call above already asserted its own ok/error outcome; this test
    // exists to print numbers, not to gate on them (see this file's own header remarks).
    assert.ok(rows.length > 0);
  });
}
