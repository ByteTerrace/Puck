import { getTopologyCoordinates, getCoordinateIndexMap, type TopologyCoordinate, type TopologyDefinition } from "../engine/evaluator";
import type { CellReference } from "./documentTools";

export type Point3 = [number, number, number];
export interface SceneCell { ref: CellReference; coordinate: TopologyCoordinate; position: Point3 }
export interface SceneProjection { cells: SceneCell[]; layers: number[]; bounds: { min: Point3; max: Point3 }; unit: number }

/** Presentation coordinates only. Native cell ordinals and logical coordinates remain untouched. */
export function projectTopology(topology: TopologyDefinition, separation = 1): SceneProjection {
  const coords = getTopologyCoordinates(topology);
  const positive = (n: number | undefined, fallback: number) => {
    if (n === undefined) return fallback;
    if (!Number.isFinite(n) || n <= 0) throw new Error("Cell size and layer height must be finite positive numbers.");
    return n;
  };
  const unit = positive(topology.cellSize, 1);
  const height = positive(topology.layerHeight, unit) * Math.max(1, Math.min(4, separation));
  const origin = topology.origin ?? [0, 0, 0];
  if (origin.length !== 3 || !origin.every(Number.isFinite)) throw new Error("Topology origin needs three finite coordinates.");
  const cells = coords.map((coordinate, index): SceneCell => {
    const { x, y, z } = coordinate;
    let px = x * unit, pz = y * unit;
    if (topology.$type === "hex") { px = (x - y / 2) * unit; pz = y * Math.sqrt(3) / 2 * unit; }
    if (topology.$type === "ring") {
      const angle = index / Math.max(1, coords.length) * Math.PI * 2;
      const radius = Math.max(unit, coords.length * unit / (Math.PI * 2));
      px = Math.sin(angle) * radius; pz = -Math.cos(angle) * radius;
    }
    return { ref: { topology: topology.name, index }, coordinate, position: [px + origin[0], z * height + origin[2], pz + origin[1]] };
  });
  if (cells.some(cell => cell.position.some(n => !Number.isFinite(n)))) throw new Error("Topology placement cannot be displayed as finite coordinates.");
  const min: Point3 = [0, 0, 0], max: Point3 = [0, 0, 0];
  for (let axis = 0; axis < 3; axis++) {
    min[axis] = cells.length ? Math.min(...cells.map(c => c.position[axis])) - unit / 2 : -unit;
    max[axis] = cells.length ? Math.max(...cells.map(c => c.position[axis])) + unit / 2 : unit;
  }
  // Center the presentation near the origin to retain GPU precision for translated documents.
  const center = min.map((n, axis) => n + (max[axis] - n) / 2);
  for (const cell of cells) cell.position = cell.position.map((n, axis) => n - center[axis]) as Point3;
  return { cells, layers: [...new Set(coords.map(c => c.z))].sort((a, b) => a - b),
    bounds: { min: min.map((n, a) => n - center[a]) as Point3, max: max.map((n, a) => n - center[a]) as Point3 }, unit };
}

/** Optional authored direction overlay, bounded independently from scene instances. */
export function projectRelationships(topology: TopologyDefinition, scene: SceneProjection): Float32Array {
  const coords = getTopologyCoordinates(topology), lookup = getCoordinateIndexMap(coords);
  const seen = new Set<string>(), points: number[] = [];
  for (const cell of scene.cells) for (const direction of topology.directions ?? []) {
    const c = cell.coordinate;
    const index = topology.$type === "ring"
      ? ((cell.ref.index + direction.x) % coords.length + coords.length) % coords.length
      : lookup.get([c.x + direction.x, c.y + direction.y, c.z + direction.z].join(","));
    if (index === undefined || index === cell.ref.index) continue;
    const key = [Math.min(index, cell.ref.index), Math.max(index, cell.ref.index)].join(":");
    if (seen.has(key)) continue;
    if (seen.size >= 16384) break;
    seen.add(key); points.push(...cell.position, ...scene.cells[index].position);
  }
  return new Float32Array(points);
}
