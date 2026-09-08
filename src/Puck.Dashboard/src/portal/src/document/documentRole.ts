/**
 * Classifies a parsed puck.world.def.v1 document's role from its own content — the studio
 * machine's own source of truth once a document is open, per its own remarks ("the role is read
 * off the document, never chosen by the user").
 *
 * The real, authoritative classifier is `OfficialWorldDocumentScanner.TryDescribeDocument`
 * (src/Puck.Cli/Official/OfficialWorldDocumentScanner.cs): a "shards/" path prefix means Shard, the
 * exact file name "standard.basis.json" means Basis, otherwise a present `documentId` means World
 * and anything else means Fragment. That classifier reads a file PATH this function never sees —
 * `readDocumentRole` sees only a parsed document `value` — so it approximates two of those four
 * arms from content alone:
 *
 *   - `exports` carrying at least one non-empty name in reads/actions/bindings -> 'fragment'
 *   - otherwise a non-empty `documentId` -> 'world'
 *   - otherwise -> 'basis' (this function's catch-all for "neither", not a filename match)
 *
 * A real shard document (see src/Puck.World/Assets/worlds/shards/*.world.json) carries a
 * `documentId` and a `basis` pointer exactly like the island root itself — nothing in its OWN
 * content distinguishes it from a root world document — so `readDocumentRole` reads a shard as
 * 'world'. A caller that already knows a document's manifest-declared role (`OPEN_OFFICIAL`'s own
 * `ManifestDocumentEntry.role`) keeps asserting that role for as long as it can; this function is
 * the fallback for a document with no better source (freshly pasted or imported text), and the
 * one re-read off a document's OWN content after every edit — an edit that touches neither
 * `documentId` nor `exports` on an opened shard keeps displaying as 'world' from then on. This is
 * a stated, deliberate simplification, not a silent gap.
 */
import type { DocumentRole } from "../official/manifest";

export type { DocumentRole };

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasNonEmptyExports(exports: unknown): boolean {
  if (!isRecord(exports)) {
    return false;
  }
  for (const facet of ["reads", "actions", "bindings"] as const) {
    const list = exports[facet];
    if (Array.isArray(list) && list.some((entry) => typeof entry === "string" && entry.length > 0)) {
      return true;
    }
  }
  return false;
}

export function readDocumentRole(value: unknown): DocumentRole {
  if (!isRecord(value)) {
    return "basis";
  }
  if (hasNonEmptyExports(value.exports)) {
    return "fragment";
  }
  if (typeof value.documentId === "string" && value.documentId.length > 0) {
    return "world";
  }
  return "basis";
}

/** Whether `value` composes over something else — a `basis` pointer, or a non-empty `imports`
 * list (see WorldDefinition's own `basis`/`imports` members). A standalone document (neither) is
 * the only shape `engine.parse` alone can validate; anything else needs `engine.composeTree`
 * against the wider official document set. */
export function hasComposition(value: unknown): boolean {
  if (!isRecord(value)) {
    return false;
  }
  if (typeof value.basis === "string" && value.basis.length > 0) {
    return true;
  }
  const imports = value.imports;
  return Array.isArray(imports) && imports.length > 0;
}

/** The official island's fixed root document name (`OfficialWorldDocumentScanner.RootDocumentName`
 * on the C# side) — every `composeTree` call in this studio composes against this root, substituting
 * whichever document is under edit by name. */
export const ISLAND_ROOT_DOCUMENT_NAME = "puck.world.json";
