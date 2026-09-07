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

export async function computeGeometry(engine: WorldEngine, value: unknown): Promise<GeometryOutcome> {
  const geometry: Record<string, readonly EngineCell[]> = {};
  const diagnostics: EngineDiagnostic[] = [];

  for (const topology of readTopologies(value)) {
    const result = await engine.cells(topology.json);
    if (result.ok) {
      geometry[topology.name] = result.cells;
    } else {
      geometry[topology.name] = [];
      diagnostics.push({ path: `state.lattices[${topology.name}]`, message: result.error });
    }
  }

  return { geometry, diagnostics };
}
