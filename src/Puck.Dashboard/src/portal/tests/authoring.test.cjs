// Exercises this package's boundary: `authoring/documentTools.ts`, `authoring/sceneProjection.ts`, and
// `authoring/presentation.ts`. The pure document-shape helpers are tested against small synthetic documents with no
// engine at all; `sceneProjection`'s render-space projection is tested against the REAL engine's own `cells()`
// geometry — this module's whole point is to project the engine's OWN positions, never re-derive them, so a fake
// geometry would not exercise the thing that matters.
const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs');
const path = require('node:path');

require('./support/register.cjs');

const {
  decodeCellValue,
  authoredCellValues,
  emptyValueFor,
  findTopology,
  listStateRows,
  listTopologies,
  parseCellAddresses,
  parseRowValue,
  rowsBoundToTopology,
  topologyDirections,
} = require('../src/authoring/documentTools.ts');
const { isVolumetric, projectDirections, projectScene } = require('../src/authoring/sceneProjection.ts');
const { PRESENTATION_KEY, appearanceFor, readPresentation } = require('../src/authoring/presentation.ts');
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

// ---------------------------------------------------------------------------------------------
// documentTools.ts — pure, no engine.
// ---------------------------------------------------------------------------------------------

test('decodeCellValue decodes number/string/boolean cell encodings to bigint, and refuses a non-integer', () => {
  assert.equal(decodeCellValue(7), 7n);
  assert.equal(decodeCellValue(-3), -3n);
  assert.equal(decodeCellValue('123456789012345678'), 123456789012345678n);
  assert.equal(decodeCellValue(true), 1n);
  assert.equal(decodeCellValue(false), 0n);
  assert.throws(() => decodeCellValue(1.5));
  assert.throws(() => decodeCellValue('not-a-number'));
});

function syntheticDocument() {
  return {
    state: {
      lattices: [
        { $type: 'grid', name: 'board', width: 2, depth: 2, cellSize: 1, origin: [0, 0, 0], directions: [{ name: 'east', x: 1, y: 0 }] },
      ],
      world: [
        {
          name: 'cell', kind: 'Int', domain: { $type: 'cellsOf', topology: 'board', empty: 0 },
          cells: [{ key: '0', value: 5 }, { key: '3', value: '9' }],
        },
        { name: 'score', kind: 'Int', value: 0, min: 0, max: 10 },
      ],
    },
  };
}

test('listTopologies/findTopology/listStateRows/rowsBoundToTopology read a synthetic document', () => {
  const document = syntheticDocument();
  assert.equal(listTopologies(document).length, 1);
  assert.equal(findTopology(document, 'board')?.name, 'board');
  assert.equal(findTopology(document, 'missing'), undefined);
  assert.equal(listStateRows(document).length, 2);

  const bound = rowsBoundToTopology(document, 'board');
  assert.equal(bound.length, 1);
  assert.equal(bound[0].name, 'cell');

  const directions = topologyDirections(findTopology(document, 'board'));
  assert.deepEqual(directions, [{ name: 'east', x: 1, y: 0 }]);
});

test('documentTools degrades gracefully — never throws — on a document that is not yet shaped like a WorldDefinition', () => {
  assert.deepEqual(listTopologies(null), []);
  assert.deepEqual(listTopologies(undefined), []);
  assert.deepEqual(listTopologies(5), []);
  assert.deepEqual(listTopologies('not an object'), []);
  assert.deepEqual(listStateRows({}), []);
  assert.deepEqual(listStateRows({ state: null }), []);
  assert.deepEqual(rowsBoundToTopology({}, 'board'), []);
  assert.equal(findTopology({}, 'board'), undefined);
});

test('topologyDirections reads directions only from grid/ring/hex/box, empty for graph/tiling/field', () => {
  const grid = { $type: 'grid', name: 'g', width: 1, depth: 1, cellSize: 1, origin: [0, 0, 0], directions: [{ name: 'e', x: 1, y: 0 }] };
  assert.deepEqual(topologyDirections(grid), [{ name: 'e', x: 1, y: 0 }]);
  const graph = { $type: 'graph', name: 'g2', origin: [0, 0, 0], cellSize: 1, cells: [], directions: [], edges: [] };
  assert.deepEqual(topologyDirections(graph), []);
  const tiling = { $type: 'tiling', family: 'Triangular', radius: 1, name: 't', origin: [0, 0, 0], cellSize: 1 };
  assert.deepEqual(topologyDirections(tiling), []);
  const gridNoDirections = { $type: 'grid', name: 'g3', width: 1, depth: 1, cellSize: 1, origin: [0, 0, 0] };
  assert.deepEqual(topologyDirections(gridNoDirections), []);
});

test('authoredCellValues decodes by String(ordinal), and emptyValueFor reads the domain default', () => {
  const document = syntheticDocument();
  const row = rowsBoundToTopology(document, 'board')[0];
  assert.deepEqual([...authoredCellValues(row).entries()].sort((a, b) => a[0] - b[0]), [[0, 5n], [3, 9n]]);
  assert.equal(emptyValueFor(row), 0n);
  assert.equal(emptyValueFor(undefined), 0n);
});

test('parseRowValue validates Int bounds and Bool tokens with a readable message, and throws on a bad literal', () => {
  const row = { name: 'score', kind: 'Int', min: 0, max: 10 };
  assert.equal(parseRowValue(row, ' 5 '), 5n);
  assert.throws(() => parseRowValue(row, '11'), /maximum/);
  assert.throws(() => parseRowValue(row, '-1'));
  assert.throws(() => parseRowValue(row, 'abc'));
  const boolRow = { name: 'flag', kind: 'Bool' };
  assert.equal(parseRowValue(boolRow, 'true'), 1n);
  assert.equal(parseRowValue(boolRow, '0'), 0n);
  assert.throws(() => parseRowValue(boolRow, '2'));
});

test('parseCellAddresses parses ranges and singles, de-duplicates, sorts, and validates against the geometry', () => {
  const geometry = Array.from({ length: 16 }, (_, ordinal) => ({ ordinal, key: String(ordinal), x: 0, y: 0, z: 0 }));
  assert.deepEqual(parseCellAddresses('0-3, 5, 5, 7', geometry), [0, 1, 2, 3, 5, 7]);
  assert.throws(() => parseCellAddresses('16', geometry), /inside this topology/);
  assert.throws(() => parseCellAddresses('abc', geometry));
  assert.throws(() => parseCellAddresses('', geometry));
  assert.throws(() => parseCellAddresses('3-1', geometry), 'a descending range is refused');
});

// ---------------------------------------------------------------------------------------------
// presentation.ts — pure, no engine.
// ---------------------------------------------------------------------------------------------

test('presentation bindings read from metadata.custom.puckStudioPresentation, keyed as decimal strings', () => {
  const composed = { metadata: { custom: { [PRESENTATION_KEY]: { cell: { 7: { label: 'X', color: '#ff0000', shape: 'sphere' }, 8: { label: 'O', color: '#00ff00' } } } } } };
  const bindings = readPresentation(composed);
  assert.deepEqual(bindings.cell['7'], { label: 'X', color: '#ff0000', shape: 'sphere' });
  assert.deepEqual(bindings.cell['8'], { label: 'O', color: '#00ff00' });
  assert.equal(appearanceFor(7n, bindings.cell).label, 'X');
  assert.equal(appearanceFor(-7n, bindings.cell).label, '-7', 'an unbound value falls back to its own decimal label');
});

test('readPresentation refuses a malformed bag by name rather than silently discarding it', () => {
  assert.throws(() => readPresentation({ metadata: { custom: { [PRESENTATION_KEY]: { cell: { 'not-a-number': {} } } } } }), /not a valid appearance binding/);
  assert.throws(() => readPresentation({ metadata: { custom: { [PRESENTATION_KEY]: 'a string, not an object' } } }));
  assert.deepEqual(readPresentation({}), {}, 'a document with no metadata.custom at all reads as empty, not a refusal');
});

test('appearanceFor is deterministic for an unbound value, and zero always reads a fixed gray', () => {
  assert.equal(appearanceFor(0n).color, '#718497');
  assert.equal(appearanceFor(0n).label, '0');
  const first = appearanceFor(42n), second = appearanceFor(42n);
  assert.equal(first.color, second.color);
});

// ---------------------------------------------------------------------------------------------
// sceneProjection.ts — over the REAL engine's own cell geometry.
// ---------------------------------------------------------------------------------------------

if (!fs.existsSync(mainMjs)) {
  test(`sceneProjection (SKIPPED: no local AppBundle at ${appBundleDir} — publish Puck.World.Browser)`, { skip: true }, () => {});
} else {
  test('projectScene centers a synthetic box topology and separates its layers by exactly the requested multiplier', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const topology = {
        $type: 'box', name: 'cube', width: 2, depth: 2, layers: 2, layerHeight: 1, cellSize: 1, origin: [0, 0, 0],
        directions: [
          { name: 'east', x: 1, y: 0, z: 0 }, { name: 'west', x: -1, y: 0, z: 0 },
          { name: 'up', x: 0, y: 0, z: 1 }, { name: 'down', x: 0, y: 0, z: -1 },
        ],
      };
      const result = await engine.cells(JSON.stringify(topology));
      assert.equal(result.ok, true, JSON.stringify(result));
      assert.equal(result.cells.length, 8);
      assert.equal(isVolumetric(result.cells), true);

      const scene1 = projectScene(result.cells, 1, 1);
      assert.equal(scene1.layers.length, 2);
      assert.equal(scene1.gridColumns, 2);
      assert.equal(scene1.gridRows, 2);
      const y0 = scene1.cells.find((c) => c.layerIndex === 0).position[1];
      const y1 = scene1.cells.find((c) => c.layerIndex === 1).position[1];
      assert.ok(Math.abs((y1 - y0) - 1) < 1e-6, `expected a 1-unit layer gap, got ${y1 - y0}`);

      const scene3 = projectScene(result.cells, 1, 3);
      const y0b = scene3.cells.find((c) => c.layerIndex === 0).position[1];
      const y1b = scene3.cells.find((c) => c.layerIndex === 1).position[1];
      assert.ok(Math.abs((y1b - y0b) - 3) < 1e-6, `expected a 3-unit layer gap, got ${y1b - y0b}`);

      // The scene is re-centered near the origin (not left at the topology's own authored origin).
      const meanX = scene1.cells.reduce((sum, c) => sum + c.position[0], 0) / scene1.cells.length;
      const meanZ = scene1.cells.reduce((sum, c) => sum + c.position[2], 0) / scene1.cells.length;
      assert.ok(Math.abs(meanX) < 1e-6, meanX);
      assert.ok(Math.abs(meanZ) < 1e-6, meanZ);

      // Every ordinal survives the projection untouched.
      assert.deepEqual(scene1.cells.map((c) => c.ordinal).sort((a, b) => a - b), result.cells.map((c) => c.ordinal).sort((a, b) => a - b));

      // Direction overlay: an axis-aligned box gets exact neighbor pairs (multiple of 6 floats per segment).
      const lines = projectDirections(topology, scene1);
      assert.ok(lines.length > 0, 'a box topology with directions must produce at least one segment');
      assert.equal(lines.length % 6, 0);
    } finally {
      await engine.dispose();
    }
  });

  test('projectDirections declines to draw an overlay for a non-axis-aligned topology (hex)', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const topology = { $type: 'hex', name: 'h', radius: 1, cellSize: 1, origin: [0, 0, 0], directions: [{ name: 'e', x: 1, y: 0 }, { name: 'w', x: -1, y: 0 }] };
      const result = await engine.cells(JSON.stringify(topology));
      assert.equal(result.ok, true, JSON.stringify(result));
      const scene = projectScene(result.cells, 1, 1);
      assert.equal(isVolumetric(result.cells), false);
      assert.equal(projectDirections(topology, scene).length, 0);
    } finally {
      await engine.dispose();
    }
  });

  test('projectScene over a 4x4x4 box preserves every ordinal address with no two cells colliding in render-space', async () => {
    const engine = await bootEngineFromLocalBundle(appBundleDir);
    try {
      const lattice = { $type: 'box', name: 'cube', width: 4, depth: 4, layers: 4, layerHeight: 1, cellSize: 1, origin: [0, 0, 0] };

      const result = await engine.cells(JSON.stringify(lattice));
      assert.equal(result.ok, true, JSON.stringify(result));
      assert.equal(result.cells.length, 64, 'a 4x4x4 cube');

      const scene = projectScene(result.cells, lattice.cellSize ?? 1, 1.6);
      assert.equal(scene.cells.length, 64);
      assert.deepEqual(
        scene.cells.map((c) => c.ordinal).sort((a, b) => a - b),
        result.cells.map((c) => c.ordinal).sort((a, b) => a - b),
      );
      const positions = new Set(scene.cells.map((c) => c.position.map((n) => Math.round(n * 1e4)).join(',')));
      assert.equal(positions.size, scene.cells.length, 'no two cells collapse onto the same render-space position');
      assert.equal(scene.layers.length, 4);
    } finally {
      await engine.dispose();
    }
  });
}
