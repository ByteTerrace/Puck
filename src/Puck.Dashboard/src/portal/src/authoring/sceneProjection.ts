/**
 * A pure projection of the engine's own cell geometry (`EngineCell[]` — world-space cell centres
 * `WorldEngine.cells()` already computes, per `Puck.World.Browser.Engine.BrowserTopology`; see
 * `documentTools.ts`'s own header) into render-space: cells grouped into layers along the world Y
 * axis, with an optional visual separation multiplier that is purely presentational (it never
 * touches simulation state or the document); a 2D column/row rank per cell for the accessible grid
 * view; and — only for an axis-aligned topology (`grid`/`box`, whose direction steps are exact
 * multiples of `cellSize`/one layer) — the line segments an authored direction overlay draws. A
 * `hex`, `ring`, `graph`, or `tiling` topology's own neighbour relation is the engine's own
 * axial/graph math; this module never re-derives it, so those kinds carry no direction overlay
 * rather than an approximated one. No ordinal-from-topology-kind math lives here at all: every
 * position comes from the engine's `EngineCell`, never computed from a topology's own
 * `$type`/`width`/`radius`/etc — see `documentTools.ts`'s header on `WorldTopology` for why.
 */
import type { EngineCell } from "../native/engineTypes";
import { topologyDirections, type WorldTopology } from "./documentTools";

export type Point3 = readonly [number, number, number];

export interface SceneCell {
  readonly ordinal: number;
  readonly key: string;
  /** Render-space position: the engine's own world X/Z, world Y replaced by
   * `layerIndex * layerGap` for visual legibility, the whole scene re-centered near the origin to
   * preserve GPU float precision for a topology authored far from world origin. */
  readonly position: Point3;
  /** Index into `SceneProjection.layers` — this cell's rank among the scene's distinct world-Y
   * values, ascending. */
  readonly layerIndex: number;
  /** This cell's rank among the scene's distinct world-X ("col") and world-Z ("row") values,
   * ascending — an accessible-grid layout, not a claim about the topology's own logical shape
   * (exact for an axis-aligned grid/box; an approximation for hex/ring/tiling/graph, good enough
   * for keyboard paging). */
  readonly grid: { readonly col: number; readonly row: number };
}

export interface SceneProjection {
  readonly cells: readonly SceneCell[];
  /** Distinct world-Y values the source cells carried, ascending — one per `layerIndex`. */
  readonly layers: readonly number[];
  readonly bounds: { readonly min: Point3; readonly max: Point3 };
  readonly unit: number;
  /** The render-space distance between adjacent `layerIndex` values — `unit * clamp(separation, 1, 4)`. */
  readonly layerGap: number;
  readonly gridColumns: number;
  readonly gridRows: number;
}

const EMPTY_PROJECTION: SceneProjection = {
  cells: [],
  layers: [],
  bounds: { min: [-0.5, -0.5, -0.5], max: [0.5, 0.5, 0.5] },
  unit: 1,
  layerGap: 1,
  gridColumns: 1,
  gridRows: 1,
};

/** Buckets a float to 4 decimal places — robust against the float32->float64 promotion noise the
 * engine's own `BrowserCellGeometry` (a `float` triple) introduces, while still distinguishing any
 * cellSize an author would realistically choose. */
function bucket(value: number): number {
  return Math.round(value * 10_000) / 10_000;
}

function ranks(sorted: readonly number[]): ReadonlyMap<number, number> {
  return new Map(sorted.map((value, index) => [value, index]));
}

/** True when `cells` occupies more than one world-Y layer — the "volumetric" test a viewport uses
 * to choose `SpatialTopology3D` over `UniversalTopologyView`, independent of the authoring
 * topology's own `$type` (a `graph` topology's cells may carry genuinely 3D `centre`s too). */
export function isVolumetric(cells: readonly EngineCell[]): boolean {
  return new Set(cells.map((cell) => bucket(cell.y))).size > 1;
}

/**
 * Projects the engine's `cells` into render-space. `unit` should be the topology's own `cellSize`
 * (a positive finite number; anything else falls back to 1). `separation` (clamped to [1, 4])
 * scales the vertical gap BETWEEN layers only — it never touches a single layer's own X/Z spacing,
 * which is already exactly the engine's own geometry.
 */
export function projectScene(cells: readonly EngineCell[], unit = 1, separation = 1): SceneProjection {
  if (cells.length === 0) {
    return EMPTY_PROJECTION;
  }
  const cellUnit = (Number.isFinite(unit) && unit > 0) ? unit : 1;
  const layerGap = cellUnit * Math.max(1, Math.min(4, separation));

  const layers = [...new Set(cells.map((cell) => bucket(cell.y)))].sort((a, b) => a - b);
  const cols = [...new Set(cells.map((cell) => bucket(cell.x)))].sort((a, b) => a - b);
  const rows = [...new Set(cells.map((cell) => bucket(cell.z)))].sort((a, b) => a - b);
  const layerRanks = ranks(layers), colRanks = ranks(cols), rowRanks = ranks(rows);

  const raw = cells.map((cell) => ({
    ordinal: cell.ordinal,
    key: cell.key,
    layerIndex: layerRanks.get(bucket(cell.y))!,
    grid: { col: colRanks.get(bucket(cell.x))!, row: rowRanks.get(bucket(cell.z))! },
    position: [cell.x, layerRanks.get(bucket(cell.y))! * layerGap, cell.z] as [number, number, number],
  }));

  const min: [number, number, number] = [Infinity, Infinity, Infinity];
  const max: [number, number, number] = [-Infinity, -Infinity, -Infinity];
  // A reduction avoids argument-count limits on large authored lattices.
  for (const cell of raw) for (let axis = 0; axis < 3; axis++) {
    min[axis] = Math.min(min[axis], cell.position[axis] - cellUnit / 2);
    max[axis] = Math.max(max[axis], cell.position[axis] + cellUnit / 2);
  }
  const center: Point3 = [min[0] + (max[0] - min[0]) / 2, min[1] + (max[1] - min[1]) / 2, min[2] + (max[2] - min[2]) / 2];

  const recentered: SceneCell[] = raw.map((cell) => ({
    ...cell,
    position: [cell.position[0] - center[0], cell.position[1] - center[1], cell.position[2] - center[2]],
  }));

  return {
    cells: recentered,
    layers,
    bounds: {
      min: [min[0] - center[0], min[1] - center[1], min[2] - center[2]],
      max: [max[0] - center[0], max[1] - center[1], max[2] - center[2]],
    },
    unit: cellUnit,
    layerGap,
    gridColumns: cols.length,
    gridRows: rows.length,
  };
}

function positionKey(position: Point3): string {
  return `${bucket(position[0])},${bucket(position[1])},${bucket(position[2])}`;
}

/**
 * Line-segment endpoint pairs (flattened `[x0,y0,z0,x1,y1,z1,...]`) for `topology`'s own authored
 * `directions`, drawn only when `topology` is axis-aligned (`grid`/`box`) — the one case where a
 * direction's `(x, y, z)` step is an EXACT multiple of `unit`/one layer rather than the engine's
 * own axial/graph math (see this module's header). Every other topology kind returns an empty
 * array rather than an approximated line. `scene` must be `projectScene`'s own answer for the same
 * cells this topology names, so the direction's step and the scene's own render-space spacing
 * agree exactly.
 */
export function projectDirections(topology: WorldTopology, scene: SceneProjection): Float32Array {
  if (topology.$type !== "grid" && topology.$type !== "box") {
    return new Float32Array(0);
  }
  const directions = topologyDirections(topology);
  if (directions.length === 0 || scene.cells.length === 0) {
    return new Float32Array(0);
  }

  const index = new Map<string, SceneCell>();
  for (const cell of scene.cells) {
    index.set(positionKey(cell.position), cell);
  }

  const seen = new Set<string>();
  const points: number[] = [];
  outer: for (const cell of scene.cells) {
    for (const direction of directions) {
      const target: Point3 = [
        cell.position[0] + direction.x * scene.unit,
        cell.position[1] + (direction.z ?? 0) * scene.layerGap,
        cell.position[2] + direction.y * scene.unit,
      ];
      const neighbor = index.get(positionKey(target));
      if (!neighbor || neighbor.ordinal === cell.ordinal) {
        continue;
      }
      const pairKey = `${Math.min(cell.ordinal, neighbor.ordinal)}:${Math.max(cell.ordinal, neighbor.ordinal)}`;
      if (seen.has(pairKey)) {
        continue;
      }
      if (seen.size >= 16384) {
        break outer;
      }
      seen.add(pairKey);
      points.push(...cell.position, ...neighbor.position);
    }
  }
  return new Float32Array(points);
}
