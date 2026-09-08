/**
 * Reads `state.lattices` — the document's own lattice topology rows (see
 * `worldDefinition.generated.ts`'s generated remarks on that member: "The lattice topologies the
 * section's lattice-shaped rows lie over") — into per-topology JSON ready for `WorldEngine.cells`.
 * Pure and engine-free: the studio machine pairs this with a live `WorldEngine.cells()` call per
 * topology to build its `geometry` context slice.
 */
import type { EngineCell } from "../native/engineTypes";
import { serializeDocumentText } from "./jsonText";

export interface TopologyEntry {
  readonly name: string;
  readonly json: string;
}

export type GeometryMap = Readonly<Record<string, readonly EngineCell[]>>;
const topologyEntries = new WeakMap<object, TopologyEntry[]>();

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Never throws on a malformed or absent `state.lattices` — an unusable document simply has no
 * topologies to compute geometry for; a per-topology `engine.cells()` failure is the machine's own
 * concern (it stores `[]` and a diagnostic for that one name, never a whole-document refusal). */
export function readTopologies(value: unknown): TopologyEntry[] {
  if (!isRecord(value)) {
    return [];
  }
  const state = value.state;
  if (!isRecord(state)) {
    return [];
  }
  const lattices = state.lattices;
  if (!Array.isArray(lattices)) {
    return [];
  }
  const cached = topologyEntries.get(lattices);
  if (cached) return cached;
  const entries: TopologyEntry[] = [];
  for (const lattice of lattices) {
    if (isRecord(lattice) && typeof lattice.name === "string" && lattice.name.length > 0) {
      entries.push({ name: lattice.name, json: serializeDocumentText(lattice) });
    }
  }
  topologyEntries.set(lattices, entries);
  return entries;
}
