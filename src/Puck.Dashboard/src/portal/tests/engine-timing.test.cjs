// Timing harness for Puck.World.Browser (Package P9 — wasm engine performance). Boots the real
// AppBundle through the same facade the studio calls (native/engineBoot.ts's own
// bootEngineFromLocalBundle — reused verbatim from tests/native.test.cjs, which is also where the
// worlds-directory-read helper below is reused from), then times every call an editing loop makes
// over the shipped island (puck.world.json + standard.basis.json + all 16 imports under
// src/Puck.World/Assets/worlds — the same document set tests/engine-wasm.test.cjs's own
// 'ComposeTree() composes the real island' test reads) and prints a table.
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
const ts = require('typescript');
const { performance } = require('node:perf_hooks');

require.extensions['.ts'] = (module, file) => module._compile(ts.transpileModule(fs.readFileSync(file, 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
}).outputText, file);

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

if (!fs.existsSync(mainMjs)) {
  test(`engine-timing (SKIPPED: no AppBundle at ${appBundleDir} — run 'dotnet publish src/Puck.World.Browser -c Release -r browser-wasm')`, { skip: true }, () => {});
} else {
  test('engine timing table: Version/Parse/ComposeTree/Compile/Judge/StateHash/Cells over the shipped island', async () => {
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

      // Parse: the tictactoe fragment under standard.basis.json (ParseFragment) — the cheap,
      // single-fragment call the document-open path takes for a bare fragment (see
      // machines/studio/document.ts's own routing remarks), timed separately from the expensive
      // full-island ComposeTree below.
      const basisJson = fs.readFileSync(path.join(worldsDir, 'standard.basis.json'), 'utf8');
      const fragmentJson = fs.readFileSync(path.join(worldsDir, 'games', 'tictactoe.world.json'), 'utf8');
      const parsed = await time('Parse (tictactoe fragment under standard.basis.json)', () => engine.parseFragment(fragmentJson, basisJson, 'a'));
      assert.equal(parsed.ok, true, JSON.stringify(parsed));

      // ComposeTree: the real shipped island — puck.world.json + standard.basis.json + all 16
      // imports. Same document set tests/engine-wasm.test.cjs's own island test reads.
      const documents = {};
      for (const file of fs.readdirSync(worldsDir, { recursive: true })) {
        if (!file.endsWith('.json')) continue;
        documents[file.split(path.sep).join('/')] = fs.readFileSync(path.join(worldsDir, file), 'utf8');
      }
      sizes.push({ label: 'documents payload (ComposeTree input, JSON.stringify(documents))', bytes: Buffer.byteLength(JSON.stringify(documents), 'utf8') });

      const composed = await time('ComposeTree (full island: puck.world.json + basis + 16 imports)', () => engine.composeTree('puck.world.json', documents));
      assert.equal(composed.ok, true, JSON.stringify(composed.errors));
      assert.ok(composed.composed, 'composeTree() must record the composed standalone document text');
      sizes.push({ label: 'composed island payload (ComposeTree output .composed)', bytes: Buffer.byteLength(composed.composed, 'utf8') });

      // Compile: the composed island's raw text — never JS-round-tripped (see native.test.cjs's own
      // remarks: a JSON.parse -> JSON.stringify round trip cannot preserve tictactoe.world.json's
      // Int64.Min/MaxValue sentinels exactly, and composeTree()'s own `.composed` field is the exact
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

      // Cells: the largest topology among the shipped fragments — games/chess.world.json's own
      // state.lattices[0] ("chessBoard"), an 8x8 = 64-cell grid. hexlines.world.json's hex
      // radius-4 board (61 cells: 3*4^2 + 3*4 + 1) is the only other lattice authored in the
      // island and is smaller. Read as a plain JS object (JSON.parse of the whole fragment is safe
      // here — the lattice definition itself carries no Int64.Min/MaxValue sentinel, unlike this
      // fragment's own state rows).
      const chessFragment = JSON.parse(fs.readFileSync(path.join(worldsDir, 'games', 'chess.world.json'), 'utf8'));
      const chessBoard = chessFragment.state.lattices.find((l) => l.name === 'chessBoard');
      assert.ok(chessBoard, 'games/chess.world.json must author a "chessBoard" lattice');
      assert.equal(chessBoard.width * chessBoard.depth, 64);

      const cells = await time('Cells (largest topology: chessBoard, 8x8 grid)', () => engine.cells(JSON.stringify(chessBoard)));
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
