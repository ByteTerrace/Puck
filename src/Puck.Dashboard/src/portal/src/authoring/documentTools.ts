/**
 * Typed helpers over the generated `WorldDefinition` document shape (`document/worldDefinition.generated.ts`
 * — never a hand-written document type, per this package's own contract): topology lookup, the
 * `state.world[]` rows a topology's cells bind through (`domain.$type === "cellsOf"`), and authored
 * cell values decoded to `bigint` — every 64-bit engine value stays a `bigint` in TypeScript, never
 * a `number` (see `native/engineTypes.ts`'s own header). Pure and document-only: nothing here calls
 * the engine or touches `StudioContext` — the components in this package's boundary do that.
 *
 * Every function here takes `document: unknown` (the exact shape `machines/studio/types.ts`'s own
 * `DocumentState.value` carries — parsed JSON the ENGINE validates, never statically trusted at this
 * layer) and degrades to an empty/undefined answer on a document that is not yet shaped like a
 * `WorldDefinition`, mirroring `document/geometry.ts`'s and `document/documentRole.ts`'s own
 * defensive posture — never throwing on a document mid-edit or freshly opened.
 */
import type { EngineCell } from "../native/engineTypes";
import type { Items3, WorldDefinition } from "../document/worldDefinition.generated";

/** `WorldDefinition` itself is `{...} | null` (the generated root type), so every indexed-access
 * type below starts from `NonNullable<WorldDefinition>` — indexing the nullable union directly is
 * a TypeScript error ("null has no property"), not a document-shape question. */
type WorldDocument = NonNullable<WorldDefinition>;
type WorldState = NonNullable<WorldDocument["state"]>;

/** One `state.lattices[]` entry — grid/ring/hex/box/graph/tiling/field, whichever `$type` the
 * document authors. */
export type WorldTopology = NonNullable<WorldState["lattices"]>[number];

/** One `state.world[]` row. */
export type WorldStateRow = NonNullable<WorldState["world"]>[number];

/** One authored `state.world[].cells[]` entry. */
export type WorldStateCell = NonNullable<WorldStateRow["cells"]>[number];

/** One authored direction of a discrete lattice topology (`Items3`, non-null). */
export type WorldDirection = NonNullable<Items3>;

/** A `state.world[]` row's `cellsOf` domain variant — the shape `rowsBoundToTopology`/`canPaintRow`/
 * `emptyValueFor` narrow to. */
export type CellsOfDomain = Extract<NonNullable<WorldStateRow["domain"]>, { $type?: "cellsOf" }>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isCellsOfDomain(domain: WorldStateRow["domain"]): domain is CellsOfDomain {
  return domain != null && domain.$type === "cellsOf";
}

/** Every `state.lattices[]` entry, in document order — `[]` for a document with no `state.lattices`
 * (including one that is not yet a record at all). */
export function listTopologies(document: unknown): readonly WorldTopology[] {
  if (!isRecord(document) || !isRecord(document.state)) {
    return [];
  }
  const lattices = document.state.lattices;
  return Array.isArray(lattices) ? (lattices as WorldTopology[]).filter((topology) => isRecord(topology)) : [];
}

/** Finds `state.lattices[]`'s entry named `name`, or `undefined`. */
export function findTopology(document: unknown, name: string): WorldTopology | undefined {
  return listTopologies(document).find((topology) => topology.name === name);
}

/** Every `state.world[]` row, in document order — `[]` for a document with no `state.world`. */
export function listStateRows(document: unknown): readonly WorldStateRow[] {
  if (!isRecord(document) || !isRecord(document.state)) {
    return [];
  }
  const rows = document.state.world;
  return Array.isArray(rows) ? (rows as WorldStateRow[]).filter((row) => isRecord(row)) : [];
}

/** The `state.world[]` rows whose `domain` is `cellsOf` the named topology — the rows a spatial
 * viewport's inspector may select/paint cells of. */
export function rowsBoundToTopology(document: unknown, topologyName: string): readonly WorldStateRow[] {
  return listStateRows(document).filter((row) => isCellsOfDomain(row.domain) && row.domain.topology === topologyName);
}

/** Every authored direction of `topology` — `[]` for a `graph`/`tiling`/`field` topology (which
 * carry no `directions` member) or one that declares none. Narrows by `$type` rather than an `in`
 * check: this generated union is large enough that structural `in` narrowing does not reliably
 * resolve the member's own array element type under this project's pinned TypeScript version. */
export function topologyDirections(topology: WorldTopology): readonly WorldDirection[] {
  if (topology.$type !== "grid" && topology.$type !== "ring" && topology.$type !== "hex" && topology.$type !== "box") {
    return [];
  }
  const directions: (Items3)[] | null | undefined = topology.directions;
  return (directions ?? []).filter((direction): direction is WorldDirection => direction != null);
}

/** Whether `row` is paintable by ordinal: a `cellsOf` domain over an `Int` or `Bool` kind — the
 * only kinds a cell address can hold a document-encodable value for (`Fixed` cells store raw
 * Q48.16 bits this module never guesses at decoding; `Text` cells hold no numeric paint value). */
export function canPaintRow(row: WorldStateRow): boolean {
  return isCellsOfDomain(row.domain) && (row.kind === "Int" || row.kind === "Bool");
}

/** The value an unauthored cell of `row` reads as — the `cellsOf` domain's own `empty` (default 0)
 * when `row` carries one, else `0n`. */
export function emptyValueFor(row: WorldStateRow | undefined): bigint {
  if (row && isCellsOfDomain(row.domain) && row.domain.empty !== undefined) {
    return decodeCellValue(row.domain.empty);
  }
  return 0n;
}

/** Decodes a `state.world[].cells[].value` (or `.min`/`.max`, same encoding for an `Int` row) into
 * the `bigint` every engine-side value is. */
export function decodeCellValue(value: number | string | boolean): bigint {
  if (typeof value === "boolean") {
    return value ? 1n : 0n;
  }
  if (typeof value === "string") {
    if (!/^-?\d+$/.test(value)) {
      throw new Error(`'${value}' is not a decimal integer.`);
    }
    return BigInt(value);
  }
  if (!Number.isInteger(value)) {
    throw new Error(`${value} is not an integer cell value.`);
  }
  return BigInt(value);
}

/** The authored value at cell ordinal `ordinal` of `row`, or `undefined` when that ordinal carries
 * no authored cell (its value comes from the row's own domain-declared default instead — see
 * `emptyValueFor`). Looks up by `String(ordinal)`, exactly how `PAINT_CELLS`/`paintCells` key
 * `cells[]` (see `machines/studio/document.ts`'s own remarks). */
export function authoredCellValue(row: WorldStateRow | undefined, ordinal: number): bigint | undefined {
  const cell = row?.cells?.find((candidate) => candidate.key === String(ordinal));
  return cell === undefined ? undefined : decodeCellValue(cell.value);
}

/** Every authored cell of `row`, decoded, keyed by ordinal — for building a fast per-topology
 * value lookup once per render instead of re-scanning `cells[]` per cell. */
export function authoredCellValues(row: WorldStateRow | undefined): ReadonlyMap<number, bigint> {
  const values = new Map<number, bigint>();
  for (const cell of row?.cells ?? []) {
    const ordinal = Number(cell.key);
    if (Number.isInteger(ordinal)) {
      values.set(ordinal, decodeCellValue(cell.value));
    }
  }
  return values;
}

/** Parses a paint value typed for `row` — a decimal integer, or `true`/`false`/`0`/`1` for a
 * `Bool` row — and checks it against the row's own declared bounds. Throws a message fit to show
 * the author directly. */
export function parsePaintValue(row: WorldStateRow, raw: string): bigint {
  const trimmed = raw.trim();
  if (row.kind === "Bool") {
    if (!["0", "1", "true", "false"].includes(trimmed)) {
      throw new Error("Use true, false, 0 or 1.");
    }
    return (trimmed === "1" || trimmed === "true") ? 1n : 0n;
  }
  if (!/^-?\d+$/.test(trimmed)) {
    throw new Error("Enter a whole decimal integer.");
  }
  const value = BigInt(trimmed);
  if (row.min !== undefined && value < decodeCellValue(row.min)) {
    throw new Error(`Value is below this state's declared minimum (${row.min}).`);
  }
  if (row.max !== undefined && value > decodeCellValue(row.max)) {
    throw new Error(`Value is above this state's declared maximum (${row.max}).`);
  }
  if (row.nonNegative && value < 0n) {
    throw new Error("Value must be non-negative.");
  }
  return value;
}

/**
 * Parses "0-19, 32, 63" into an ascending, de-duplicated ordinal list, validated against
 * `geometry` — the engine's own `cells()` answer for the topology, the one source of truth for
 * which ordinals actually exist. Never a topology-kind-specific ordinal formula: see this
 * package's `sceneProjection.ts` header on why no such formula lives in this codebase any more.
 * Relies on the engine's own contiguous-ordinal invariant (`0..cells.length-1`, see
 * `Puck.World.Browser.Engine.BrowserTopology`) to bound-check cheaply without building a set of
 * every valid ordinal first.
 */
export function parseCellAddresses(text: string, geometry: readonly EngineCell[]): number[] {
  if (text.length > 32768) {
    throw new Error("Address selection is too long.");
  }
  const cellCount = geometry.length;
  const ordinals = new Set<number>();
  for (const part of text.split(",")) {
    const match = /^\s*(\d+)\s*(?:-\s*(\d+)\s*)?$/.exec(part);
    if (!match) {
      throw new Error("Use cell addresses or inclusive ranges, such as 0-15, 32, 63.");
    }
    const from = Number(match[1]);
    const to = Number(match[2] ?? match[1]);
    if (!Number.isSafeInteger(from) || !Number.isSafeInteger(to) || from > to || to >= cellCount) {
      throw new Error(`Cell addresses must be inside this topology (0-${Math.max(0, cellCount - 1)}), in ascending ranges.`);
    }
    for (let ordinal = from; ordinal <= to; ordinal++) {
      ordinals.add(ordinal);
    }
  }
  if (ordinals.size === 0) {
    throw new Error("Enter at least one cell address.");
  }
  return [...ordinals].sort((a, b) => a - b);
}
