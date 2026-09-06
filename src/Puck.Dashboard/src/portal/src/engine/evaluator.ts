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

// Generate (x, y, z) coordinate array for a topology if not explicitly provided
export function getTopologyCoordinates(topo: TopologyDefinition): TopologyCoordinate[] {
  if (topo.coordinates && topo.coordinates.length > 0) {
    return topo.coordinates;
  }

  const width = topo.dimensions?.x ?? topo.width ?? 4;
  const depth = topo.dimensions?.y ?? topo.depth ?? 4;
  const layers = topo.dimensions?.z ?? topo.layers ?? (topo.$type === "box" ? 4 : 1);

  const coords: TopologyCoordinate[] = [];
  for (let z = 0; z < layers; z++) {
    for (let y = 0; y < depth; y++) {
      for (let x = 0; x < width; x++) {
        coords.push({ x, y, z });
      }
    }
  }
  return coords;
}

// Map from coordinate key string "x,y,z" to cell index
export function getCoordinateIndexMap(coords: TopologyCoordinate[]): Map<string, number> {
  const map = new Map<string, number>();
  coords.forEach((c, idx) => {
    map.set(`${c.x},${c.y},${c.z}`, idx);
  });
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
      const targetX = srcCoord.x + dir.x;
      const targetY = srcCoord.y + dir.y;
      const targetZ = srcCoord.z + dir.z;

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
  targetVal: number
): bigint {
  let mask = 0n;
  for (let i = 0; i < 64; i++) {
    const val = Array.isArray(boardCells) ? boardCells[i] : boardCells[i];
    if (val === targetVal) {
      mask |= (1n << BigInt(i));
    }
  }
  return mask;
}

// Tokenize and evaluate expression string with BigInt and arithmetic support
export function evaluatePuckExpression(
  expression: string,
  state: Record<string, any>,
  boardCells: Record<string, Record<number, number>>,
  topologies: Record<string, TopologyDefinition>
): any {
  const trimmed = expression.trim();
  if (!trimmed) return 0;

  // 1. Literal numbers
  if (/^-?\d+n?$/.test(trimmed)) {
    return trimmed.endsWith("n") ? BigInt(trimmed.slice(0, -1)) : Number(trimmed);
  }

  // 2. $board:mask:boardState:targetValue:maskBit
  const boardMaskMatch = trimmed.match(/^\$board:mask:([^:]+):(-?\d+):(-?\d+)$/);
  if (boardMaskMatch) {
    const [, boardName, targetValStr] = boardMaskMatch;
    const targetVal = Number(targetValStr);
    const cells = boardCells[boardName] ?? {};
    return computeBoardMask(cells, targetVal);
  }

  // 3. Simple state read
  if (/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(trimmed)) {
    const val = state[trimmed];
    return val !== undefined ? val : 0;
  }

  // Pre-process function calls like boardShift(arg, topo, dir)
  let processed = trimmed;

  // Recursively expand boardShift calls from innermost outwards
  const shiftRegex = /boardShift\s*\(\s*([^,]+?)\s*,\s*([a-zA-Z0-9_]+)\s*,\s*([a-zA-Z0-9_]+)\s*\)/;
  let guard = 0;
  while (shiftRegex.test(processed) && guard++ < 30) {
    processed = processed.replace(shiftRegex, (_, subExpr, topoName, dirName) => {
      const subVal = evaluatePuckExpression(subExpr, state, boardCells, topologies);
      const maskBigInt = typeof subVal === "bigint" ? subVal : BigInt(subVal || 0);
      const topo = topologies[topoName];
      const result = boardShift(maskBigInt, topo, dirName);
      return `${result.toString()}n`;
    });
  }

  // Replace $board:mask references inside compound expressions
  processed = processed.replace(
    /\$board:mask:([a-zA-Z0-9_]+):(-?\d+):(-?\d+)/g,
    (_, bName, tVal) => {
      const mask = computeBoardMask(boardCells[bName] ?? {}, Number(tVal));
      return `${mask.toString()}n`;
    }
  );

  // Substitute state variables with BigInt/number representation
  // Sort keys by length descending to prevent substring collisions
  const varNames = Object.keys(state).sort((a, b) => b.length - a.length);
  for (const vName of varNames) {
    const val = state[vName];
    const regex = new RegExp(`\\b${vName}\\b`, "g");
    if (typeof val === "bigint") {
      processed = processed.replace(regex, `${val.toString()}n`);
    } else if (typeof val === "number") {
      processed = processed.replace(regex, `${val}`);
    } else if (typeof val === "boolean") {
      processed = processed.replace(regex, val ? "true" : "false");
    }
  }

  try {
    // Safely evaluate standard mathematical/bitwise expressions
    // Normalizing BigInt operations where needed
    // In JS: (BigInt != 0n) produces boolean
    const fn = new Function(`
      try {
        const res = (${processed});
        if (typeof res === "bigint") {
          return res <= BigInt(Number.MAX_SAFE_INTEGER) && res >= BigInt(Number.MIN_SAFE_INTEGER)
            ? Number(res)
            : res;
        }
        if (typeof res === "boolean") return res ? 1 : 0;
        return res;
      } catch (e) {
        return 0;
      }
    `);
    return fn();
  } catch {
    return 0;
  }
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

  return keyPattern;
}

// Evaluates an ActionPredicate tree against live state
export function evaluatePuckPredicate(
  pred: ActionPredicate | null | undefined,
  state: Record<string, any>,
  boardCells: Record<string, Record<number, number>>
): { passed: boolean; details: string } {
  if (!pred) return { passed: true, details: "No gate condition" };

  if (pred.$type === "all") {
    const children = pred.predicates ?? [];
    for (const child of children) {
      const res = evaluatePuckPredicate(child, state, boardCells);
      if (!res.passed) {
        return { passed: false, details: `ALL failed on: ${res.details}` };
      }
    }
    return { passed: true, details: "ALL passed" };
  }

  if (pred.$type === "any") {
    const children = pred.predicates ?? [];
    for (const child of children) {
      const res = evaluatePuckPredicate(child, state, boardCells);
      if (res.passed) {
        return { passed: true, details: `ANY passed on: ${res.details}` };
      }
    }
    return { passed: false, details: "ANY failed (no predicate passed)" };
  }

  if (pred.$type === "not") {
    const res = evaluatePuckPredicate(pred.predicate, state, boardCells);
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

    if (leftVal === undefined) leftVal = 0;

    let rightVal =
      pred.comparandState !== undefined
        ? state[pred.comparandState] ?? 0
        : pred.value ?? 0;

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
        passed = Number(leftVal) > Number(rightVal);
        break;
      case "GreaterOrEqual":
        passed = Number(leftVal) >= Number(rightVal);
        break;
      case "Less":
        passed = Number(leftVal) < Number(rightVal);
        break;
      case "LessOrEqual":
        passed = Number(leftVal) <= Number(rightVal);
        break;
      default:
        passed = true;
    }

    const desc = `${stateName}${pred.key ? `[${pred.key}]` : ""} (${leftVal}) ${op} ${pred.comparandState ?? String(pred.value)} (${rightVal})`;
    return { passed, details: desc };
  }

  return { passed: true, details: "True" };
}
