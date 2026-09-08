/**
 * Pairs `document/geometry.ts`'s pure `readTopologies` with a live `WorldEngine.cells()` call per
 * topology, folding the result into the machine's `geometry` context slice. A single topology's
 * `cells()` failure stores `[]` for that name plus its own diagnostic — never a whole-document
 * refusal, since the rest of the document may still be perfectly good.
 */
import { readTopologies } from "../../document/geometry";
import type { EngineCell, EngineDiagnostic, WorldEngine } from "../../native/engineTypes";

export interface GeometryOutcome {
  readonly geometry: Readonly<Record<string, readonly EngineCell[]>>;
  readonly diagnostics: readonly EngineDiagnostic[];
}

// Retain only the most recently requested topology set for each engine. Unrelated document
// edits reuse both engine answers and array identities, keeping the viewport camera stable.
const cachedGeometry = new WeakMap<WorldEngine, Map<string, Promise<Awaited<ReturnType<WorldEngine["cells"]>>>>>();

export async function computeGeometry(engine: WorldEngine, value: unknown): Promise<GeometryOutcome> {
  const geometry: Record<string, readonly EngineCell[]> = {};
  const diagnostics: EngineDiagnostic[] = [];
  const previous = cachedGeometry.get(engine);
  const current = new Map<string, Promise<Awaited<ReturnType<WorldEngine["cells"]>>>>();
  const topologies = readTopologies(value);
  for (const topology of topologies) {
    const pending = previous?.get(topology.json) ?? engine.cells(topology.json).catch(error => ({
      ok: false as const, error: error instanceof Error ? error.message : String(error),
    }));
    current.set(topology.json, pending);
  }
  cachedGeometry.set(engine, current);

  for (const topology of topologies) {
    const result = await current.get(topology.json)!;
    if (result.ok) {
      geometry[topology.name] = result.cells;
    } else {
      current.delete(topology.json);
      geometry[topology.name] = [];
      diagnostics.push({ path: `state.lattices[${topology.name}]`, message: result.error });
    }
  }

  return { geometry, diagnostics };
}
