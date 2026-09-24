/**
 * Address a location inside a parsed JSON document by an ordered list of object keys
 * and array indices. The root document is addressed by the empty path.
 */
export type JsonPath = ReadonlyArray<string | number>;

/** Reads the value at `path`, or `undefined` if any segment along the way is absent. */
export function getAt(root: unknown, path: JsonPath): unknown {
  let current: unknown = root;
  for (const segment of path) {
    if (current === null || current === undefined) return undefined;
    current = (current as Record<string | number, unknown>)[segment as never];
  }
  return current;
}

/** Escapes one reference token per RFC 6901 (`~` before `/`, in that order). */
function escapeToken(token: string): string {
  return token.replace(/~/g, "~0").replace(/\//g, "~1");
}

function unescapeToken(token: string): string {
  return token.replace(/~1/g, "/").replace(/~0/g, "~");
}

/** A token made only of decimal digits (no leading zero unless it is exactly "0") is an array index. */
const ARRAY_INDEX_TOKEN = /^(?:0|[1-9]\d*)$/;

/** Renders `path` as an RFC 6901 JSON Pointer, e.g. `["state","world",3]` -> `"/state/world/3"`. */
export function pathToPointer(path: JsonPath): string {
  if (path.length === 0) return "";
  return "/" + path.map(segment => escapeToken(String(segment))).join("/");
}

/**
 * Parses an RFC 6901 JSON Pointer back into a {@link JsonPath}. A purely-decimal token
 * becomes a number (an array index); anything else stays a string (an object key). This is
 * the same ambiguity the pointer format itself carries — a numeric-looking OBJECT key is
 * indistinguishable from an array index without the document to disambiguate against.
 */
export function pointerToPath(pointer: string): JsonPath {
  if (pointer === "") return [];
  if (!pointer.startsWith("/")) throw new Error(`Invalid JSON pointer: ${pointer}`);
  return pointer
    .slice(1)
    .split("/")
    .map(unescapeToken)
    .map(token => (ARRAY_INDEX_TOKEN.test(token) ? Number(token) : token));
}
