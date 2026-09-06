import { previewExpression } from "./previewExpression";

export interface TopologyCoordinate {
  x: number;
  y: number;
  z: number;
}

export interface DirectionDefinition {
  name: string;
  x: number;
  y: number;
  z: number;
}

export interface TopologyDefinition {
  $type: "grid" | "ring" | "hex" | "box" | "lattice";
  name: string;
  width?: number;
  depth?: number;
  layers?: number;
  radius?: number;
  cellSize?: number;
  layerHeight?: number;
  origin?: [number, number, number];
  dimensions?: { x?: number; y?: number; z?: number };
  coordinates?: TopologyCoordinate[];
  directions?: DirectionDefinition[];
}

export interface ActionPredicate {
  $type: string;
  comparison?: string;
  state?: string;
  comparandState?: string;
  value?: number | string | bigint;
  key?: string;
  predicates?: ActionPredicate[];
  predicate?: ActionPredicate;
}

export type PuckPredicate = ActionPredicate;

export interface ActionEffect {
  $type: string;
  state: string;
  fromState?: string;
  value?: number | string | bigint;
  expression?: string;
  key?: string;
}

export interface WorldRule {
  name: string;
  mode?: "Level" | "Edge";
  forEach?: string | null;
  gate?: ActionPredicate | null;
  effects?: ActionEffect[];
}

// Preview geometry is bounded and cached by immutable topology identity.
export const MAX_PREVIEW_CELLS = 4096;
const coordinateCache = new WeakMap<TopologyDefinition, TopologyCoordinate[]>();
const indexCache = new WeakMap<TopologyCoordinate[], Map<string, number>>();
export function getTopologyCoordinates(topo: TopologyDefinition): TopologyCoordinate[] {
  if (!["grid", "ring", "hex", "box", "lattice"].includes(topo.$type)) throw new Error("Unsupported preview topology.");
  if (topo.$type === "lattice" && !topo.coordinates?.length) return [];
  const cached = coordinateCache.get(topo);
  if (cached) return cached;
  const size = (value: number | undefined, fallback: number) => {
    const n = value ?? fallback;
    if (!Number.isInteger(n) || n < 1 || n > MAX_PREVIEW_CELLS) throw new Error("Invalid preview topology size.");
    return n;
  };
  let coords: TopologyCoordinate[] = [];
  if (topo.coordinates?.length) {
    if (topo.coordinates.length > MAX_PREVIEW_CELLS) throw new Error("Preview supports up to 4,096 cells.");
    coords = topo.coordinates;
  } else if (topo.$type === "hex") {
    const radius = size(topo.radius, 3);
    if (1 + 3 * radius * (radius + 1) > MAX_PREVIEW_CELLS) throw new Error("Preview supports up to 4,096 cells.");
    coords.push({x: 0, y: 0, z: 0});
    // The native HexagonalIndex.Decode order and Eisenstein basis.
    for (let r = 1; r <= radius; r++) for (let i = 0; i < 6 * r; i++) {
      const offset = (i - (r - 1) + 6 * r) % (6 * r);
      const side = Math.floor(offset / r), k = offset % r;
      const [x, y] = [[r,k],[r-k,r],[-k,r-k],[-r,-k],[k-r,-r],[k,k-r]][side];
      coords.push({x:x || 0,y:y || 0,z:0});
    }
  } else {
    const width = size(topo.width ?? topo.dimensions?.x, 4);
    const depth = topo.$type === "ring" ? 1 : size(topo.depth ?? topo.dimensions?.y, 4);
    const layers = topo.$type === "box" ? size(topo.layers ?? topo.dimensions?.z, 4) : 1;
    if (width * depth * layers > MAX_PREVIEW_CELLS) throw new Error("Preview supports up to 4,096 cells.");
    for (let z = 0; z < layers; z++) for (let y = 0; y < depth; y++) for (let x = 0; x < width; x++) coords.push({x,y,z});
  }
  coordinateCache.set(topo, coords);
  return coords;
}

// Map from coordinate key string "x,y,z" to cell index
export function getCoordinateIndexMap(coords: TopologyCoordinate[]): Map<string, number> {
  const cached = indexCache.get(coords);
  if (cached) return cached;
  const map = new Map<string, number>();
  coords.forEach((c, idx) => {
    map.set(`${c.x},${c.y},${c.z}`, idx);
  });
  indexCache.set(coords, map);
  return map;
}

// Shifts a 64-bit BigInt mask along direction (dx, dy, dz) on topology
export function boardShift(
  mask: bigint,
  topo: TopologyDefinition,
  directionName: string
): bigint {
  if (mask === 0n || !topo) return 0n;

  const dir = (topo.directions ?? []).find((d) => d.name === directionName);
  if (!dir) return 0n;

  const coords = getTopologyCoordinates(topo);
  const indexMap = getCoordinateIndexMap(coords);

  let shifted = 0n;
  const cellCount = coords.length;

  for (let i = 0; i < cellCount; i++) {
    const bit = 1n << BigInt(i);
    if ((mask & bit) !== 0n) {
      const srcCoord = coords[i];
      const targetX = topo.$type === "ring" ? ((srcCoord.x + dir.x) % coords.length + coords.length) % coords.length : srcCoord.x + dir.x;
      const targetY = srcCoord.y + (dir.y ?? 0);
      const targetZ = srcCoord.z + (dir.z ?? 0);

      const targetIdx = indexMap.get(`${targetX},${targetY},${targetZ}`);
      if (targetIdx !== undefined) {
        shifted |= (1n << BigInt(targetIdx));
      }
    }
  }

  return shifted;
}

// Computes a 64-bit bitboard mask where bit i is set if cell boardCells[i] == targetVal
export function computeBoardMask(
  boardCells: Record<number, number> | number[],
  targetVal: number,
  upper = targetVal,
  cellCount = 64
): bigint {
  let mask = 0n;
  for (let i = 0; i < Math.min(cellCount, 64); i++) {
    const val = (boardCells as any)[i] ?? 0;
    if (val >= targetVal && val <= upper) {
      mask |= (1n << BigInt(i));
    }
  }
  return mask;
}

// This offline subset uses integer arithmetic and refuses unsupported syntax.
export function evaluatePuckExpression(expression: string, state: Record<string, any>, boardCells: Record<string, Record<number, number>>, topologies: Record<string, TopologyDefinition>): number | bigint {
  return previewExpression(expression, name => {
    const mask = name.match(/^\$board:mask:([^:]+):(-?\d+):(-?\d+)$/);
    if (mask) {
      if (!Object.hasOwn(boardCells,mask[1])) throw new Error("Unknown preview board: " + mask[1]);
      const shapes=Object.values(topologies);
      if (shapes.length !== 1) throw new Error("Mask preview currently requires one topology.");
      const count=getTopologyCoordinates(shapes[0]).length;
      if(count>64)throw new Error("Bitboard preview is limited to 64 cells.");
      return computeBoardMask(boardCells[mask[1]],Number(mask[2]),Number(mask[3]),count);
    }
    if (!Object.hasOwn(state, name)) throw new Error("Unknown preview register: " + name);
    const value = state[name];
    if (!["number", "bigint", "boolean"].includes(typeof value)) throw new Error("Unsupported preview value: " + name);
    return value;
  }, (mask, topology, direction) => {
    const topo = topologies[topology];
    if (!topo || !topo.directions?.some(d => d.name === direction)) throw new Error("Unknown preview topology or direction.");
    if (getTopologyCoordinates(topo).length > 64) throw new Error("Bitboard preview is limited to 64 cells.");
    return boardShift(mask, topo, direction);
  });
}

// Resolve key patterns like "$cell:tttMoveCell:$value"
export function resolveEffectKey(
  keyPattern: string | undefined,
  state: Record<string, any>
): string | number | null {
  if (!keyPattern) return null;

  // Pattern: "$cell:<stateVar>:$value"
  const match = keyPattern.match(/^\$cell:([^:]+):\$value$/);
  if (match) {
    const stateVar = match[1];
    return state[stateVar] !== undefined ? Number(state[stateVar]) : null;
  }

  if (/^-?\d+$/.test(keyPattern)) return Number(keyPattern);
  return keyPattern;
}

// Evaluates an ActionPredicate tree against live state
export function evaluatePuckPredicate(
  pred: ActionPredicate | null | undefined,
  state: Record<string, any>,
  boardCells: Record<string, Record<number, number>>,
  budget = {remaining:4096}, depth = 0,
): { passed: boolean; details: string } {
  if (--budget.remaining < 0 || depth > 32) throw new Error("Preview predicate budget exceeded.");
  if (!pred) return { passed: true, details: "No gate condition" };

  if (pred.$type === "all") {
    const children = pred.predicates ?? [];
    for (const child of children) {
      const res = evaluatePuckPredicate(child, state, boardCells, budget, depth + 1);
      if (!res.passed) {
        return { passed: false, details: `ALL failed on: ${res.details}` };
      }
    }
    return { passed: true, details: "ALL passed" };
  }

  if (pred.$type === "any") {
    const children = pred.predicates ?? [];
    for (const child of children) {
      const res = evaluatePuckPredicate(child, state, boardCells, budget, depth + 1);
      if (res.passed) {
        return { passed: true, details: `ANY passed on: ${res.details}` };
      }
    }
    return { passed: false, details: "ANY failed (no predicate passed)" };
  }

  if (pred.$type === "not") {
    const res = evaluatePuckPredicate(pred.predicate, state, boardCells, budget, depth + 1);
    return { passed: !res.passed, details: `NOT (${res.details})` };
  }

  if (pred.$type === "compareState") {
    const stateName = pred.state ?? "";
    let leftVal = state[stateName];

    // Check if reading from board cell key
    if (pred.key) {
      const resolvedKey = resolveEffectKey(pred.key, state);
      if (resolvedKey !== null && typeof resolvedKey === "number") {
        leftVal = boardCells[stateName]?.[resolvedKey] ?? 0;
      }
    }

    if (leftVal === undefined) throw new Error("Unknown preview register or cell key: " + stateName);

    let rightVal =
      pred.comparandState !== undefined
        ? state[pred.comparandState]
        : pred.value ?? 0;

    if (rightVal === undefined) throw new Error("Unknown comparison register.");
    const op = pred.comparison;
    let passed = false;

    switch (op) {
      case "Equal":
        passed = leftVal == rightVal;
        break;
      case "NotEqual":
        passed = leftVal != rightVal;
        break;
      case "Greater":
        passed = BigInt(leftVal) > BigInt(rightVal);
        break;
      case "GreaterOrEqual":
        passed = BigInt(leftVal) >= BigInt(rightVal);
        break;
      case "Less":
        passed = BigInt(leftVal) < BigInt(rightVal);
        break;
      case "LessOrEqual":
        passed = BigInt(leftVal) <= BigInt(rightVal);
        break;
      default:
        throw new Error("Unsupported preview comparison: " + op);
    }

    const desc = `${stateName}${pred.key ? `[${pred.key}]` : ""} (${leftVal}) ${op} ${pred.comparandState ?? String(pred.value)} (${rightVal})`;
    return { passed, details: desc };
  }

  throw new Error("Unsupported preview predicate: " + pred.$type);
}
