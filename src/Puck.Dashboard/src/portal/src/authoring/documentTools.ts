import { getTopologyCoordinates, type TopologyDefinition } from "../engine/evaluator";

/** Document addresses survive view changes; renderer instance IDs never leave the viewport. */
export interface CellReference { topology: string; index: number }
export interface StateRowDefinition {
  name: string;
  kind?: string;
  value?: number | string | boolean | bigint;
  min?: number;
  max?: number;
  nonNegative?: boolean;
  domain?: { $type?: string; topology?: string; empty?: number };
  cells?: { key: number | string; value: number; [key: string]: unknown }[];
}

export function validSelection(world: any, selection: CellReference[]): CellReference[] {
  const counts = new Map<string, number>();
  for (const topology of world.state.lattices ?? []) {
    try { counts.set(topology.name, getTopologyCoordinates(topology).length); } catch { /* Native-only geometry. */ }
  }
  const seen = new Set<string>();
  return selection.filter(ref => {
    const key = JSON.stringify([ref.topology, ref.index]);
    if (!Number.isInteger(ref.index) || ref.index < 0 || ref.index >= (counts.get(ref.topology) ?? 0) || seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Build one immutable candidate for the complete selection, then validate it at commit. */
export function paintCells(world: any, stateName: string, selection: CellReference[], value: number): any {
  const row: StateRowDefinition | undefined = world.state.world.find((s: StateRowDefinition) => s.name === stateName);
  if (!row || row.domain?.$type !== "cellsOf" || !["int", "bool"].includes(row.kind ?? "")) throw new Error("Choose an integer or boolean cell domain to paint.");
  if (!Number.isSafeInteger(value) || (row.kind === "bool" && value !== 0 && value !== 1) ||
      (row.min !== undefined && value < row.min) || (row.max !== undefined && value > row.max) || (row.nonNegative && value < 0)) {
    throw new Error("Paint value must be an exact integer within this state's declared bounds.");
  }
  if (!selection.length || validSelection(world, selection).length !== selection.length || selection.some(ref => ref.topology !== row.domain!.topology)) {
    throw new Error("Select cells belonging to this state's topology.");
  }
  const cells = new Map((row.cells ?? []).map(cell => [Number(cell.key), cell]));
  let changed = false;
  for (const ref of selection) {
    const previous = cells.get(ref.index);
    if ((previous?.value ?? row.domain.empty ?? 0) === value) continue;
    changed = true;
    cells.set(ref.index, { ...previous, key: previous?.key ?? ref.index, value });
  }
  if (!changed) return world;
  return { ...world, state: { ...world.state, world: world.state.world.map((s: StateRowDefinition) => s !== row ? s : {
    ...row, cells: [...cells.values()].sort((a, b) => Number(a.key) - Number(b.key)),
  }) } };
}

export function authoredCells(row?: StateRowDefinition): Record<number, number> {
  return Object.fromEntries((row?.cells ?? []).map(cell => [Number(cell.key), cell.value]));
}

export function selectionForTopology(selection: CellReference[], topology?: TopologyDefinition): Set<number> {
  return new Set(selection.filter(ref => ref.topology === topology?.name).map(ref => ref.index));
}

/** Inclusive address ranges let keyboard users build the same bulk selections as pointer users. */
export function parseCellAddresses(text: string, topology: TopologyDefinition): CellReference[] {
  const count = getTopologyCoordinates(topology).length;
  const indices = new Set<number>();
  if (text.length > 32768) throw new Error("Address selection is too long.");
  for (const part of text.split(",")) {
    const match = /^\s*(\d+)\s*(?:-\s*(\d+)\s*)?$/.exec(part);
    if (!match) throw new Error("Use cell addresses or inclusive ranges, such as 0-15, 32, 63.");
    const from = Number(match[1]), to = Number(match[2] ?? match[1]);
    if (!Number.isSafeInteger(from) || !Number.isSafeInteger(to) || from > to || to >= count) throw new Error("Cell addresses must be inside this topology, in ascending ranges.");
    for (let index = from; index <= to; index++) indices.add(index);
  }
  return [...indices].map(index => ({ topology: topology.name, index }));
}
