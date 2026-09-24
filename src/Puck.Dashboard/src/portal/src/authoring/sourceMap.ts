/**
 * Reading the compiler's source map: from a compiled value's JSON pointer to the source span that produced it.
 * The compiler maps the values it wrote from source; a value it derived (a default, a generated member) resolves
 * through its nearest mapped ancestor, so a selection always lands on the source that is responsible for it.
 */
import type { SourceMap, SourceSpan } from "../native/engineTypes";
import { getAt, pathToPointer, pointerToPath, type JsonPath } from "../document/jsonPath";

/** The span for `pointer`, or for its nearest mapped ancestor, or `null` when nothing above it is mapped. */
export function spanAt(sourceMap: SourceMap, pointer: string): SourceSpan | null {
  let path = pointerToPath(pointer);
  for (;;) {
    const span = sourceMap[pathToPointer(path)];
    if (span) return span;
    if (path.length === 0) return null;
    path = path.slice(0, -1);
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** The path of the document's rule named `name` (`rules[i]`), or `null` when the document has none by that name. */
export function rulePath(document: unknown, name: string): JsonPath | null {
  const rules = getAt(document, ["rules"]);
  if (!Array.isArray(rules)) return null;
  const index = rules.findIndex((rule) => isRecord(rule) && rule.name === name);
  return index < 0 ? null : ["rules", index];
}

/** The source span of the rule named `name` in a compiled document: the rule's own span (the compiler maps every
 * authored rule at `/rules/<i>`). A rule another file contributes to a composition is not in this document, so it
 * resolves to nothing. */
export function ruleSpan(document: unknown, sourceMap: SourceMap, name: string): SourceSpan | null {
  const path = rulePath(document, name);
  return path ? spanAt(sourceMap, pathToPointer(path)) : null;
}
