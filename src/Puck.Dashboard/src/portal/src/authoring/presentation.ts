/**
 * The studio's own per-value presentation bag, carried in `metadata.custom.puckStudioPresentation`
 * — `metadata.custom` is a native nested object bag in the generated schema
 * (`{[k: string]: {[k: string]: unknown}}`, see `worldDefinition.generated.ts`'s own `metadata`
 * member), so no JSON-string encoding step is needed the way an ad hoc string extension would need
 * one. Bindings are keyed by state row name, then by the cell VALUE as a decimal string (`bigint`
 * cannot be an object key, and every engine value is a `bigint` — see `documentTools.ts`'s own
 * header), so a huge Int64 value binds exactly like a small one.
 */
import type { JsonPath } from "../document/jsonPath";

export const PRESENTATION_KEY = "puckStudioPresentation";

export interface ValueAppearance {
  readonly label: string;
  readonly color: string;
  readonly hidden?: boolean;
  readonly shape?: "cube" | "sphere" | "diamond";
}

/** State row name -> decimal cell-value string -> its appearance. */
export type PresentationBindings = Readonly<Record<string, Readonly<Record<string, ValueAppearance>>>>;

const PALETTE = ["#74c9ba", "#efaf91", "#a8b8f8", "#dfaad5", "#d5c67d", "#92bed4"];
const SHAPES: readonly ValueAppearance["shape"][] = ["cube", "sphere", "diamond"];

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isValueAppearance(value: unknown): value is ValueAppearance {
  if (!isRecord(value)) {
    return false;
  }
  return (
    typeof value.label === "string" && value.label.length > 0 && value.label.length <= 80 &&
    typeof value.color === "string" && /^#[\da-f]{6}$/i.test(value.color) &&
    (value.hidden === undefined || typeof value.hidden === "boolean") &&
    (value.shape === undefined || (SHAPES as readonly unknown[]).includes(value.shape))
  );
}

/** A default appearance for a value with no authored binding — a deterministic color from the
 * shared palette, so an unbound board still reads at a glance. */
export function appearanceFor(value: bigint, bindings?: Readonly<Record<string, ValueAppearance>>): ValueAppearance {
  const bound = bindings?.[value.toString()];
  if (bound) {
    return bound;
  }
  if (value === 0n) {
    return { label: "0", color: "#718497" };
  }
  const magnitude = value < 0n ? -value : value;
  const index = Number(magnitude % BigInt(PALETTE.length));
  return { label: value.toString(), color: PALETTE[index] };
}

/** Reads and validates the studio's own presentation bag. Throws a descriptive error on a
 * malformed bag rather than silently discarding author intent; returns `{}` when the document
 * carries none yet (including a document not yet shaped like a `WorldDefinition` at all). */
export function readPresentation(document: unknown): PresentationBindings {
  if (!isRecord(document) || !isRecord(document.metadata) || !isRecord(document.metadata.custom)) {
    return {};
  }
  const bag = document.metadata.custom[PRESENTATION_KEY];
  if (bag === undefined) {
    return {};
  }
  if (!isRecord(bag)) {
    throw new Error("metadata.custom.puckStudioPresentation must be an object.");
  }
  for (const [stateName, entries] of Object.entries(bag)) {
    if (!isRecord(entries)) {
      throw new Error(`metadata.custom.puckStudioPresentation.${stateName} must be an object.`);
    }
    for (const [value, entry] of Object.entries(entries)) {
      if (!/^-?\d+$/.test(value) || !isValueAppearance(entry)) {
        throw new Error(`metadata.custom.puckStudioPresentation.${stateName}.${value} is not a valid appearance binding.`);
      }
    }
  }
  return bag as unknown as PresentationBindings;
}

/** Builds the `EDIT_DOCUMENT` path/value/label for binding `value`'s appearance on `stateName` —
 * the caller sends `{type: "EDIT_DOCUMENT", ...}` itself; this module never touches `StudioContext`
 * (see this package's own boundary). Re-validates the merged bag before returning, so a caller
 * that applies the edit can trust `readPresentation` will accept it back unchanged. */
export function presentationEdit(
  document: unknown,
  stateName: string,
  value: bigint,
  appearance: ValueAppearance,
): { path: JsonPath; value: unknown; label: string } {
  const bindings = readPresentation(document);
  const nextForState = { ...(bindings[stateName] ?? {}), [value.toString()]: appearance };
  const edited = { ...bindings, [stateName]: nextForState };
  readPresentation({ metadata: { custom: { [PRESENTATION_KEY]: edited } } });
  return {
    path: ["metadata", "custom", PRESENTATION_KEY, stateName],
    value: nextForState,
    label: `bind ${stateName}=${value} appearance`,
  };
}
