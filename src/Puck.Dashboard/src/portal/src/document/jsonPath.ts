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

/**
 * Returns a new root with `value` written at `path`, sharing every untouched subtree with
 * `root` (only the ancestors of `path` are copied). A missing intermediate container is
 * created fresh, as an array if the SEGMENT that indexes into it is a number, or an object
 * if it is a string — the segment addressing a container is what implies that container's
 * shape. Setting an array index past the current length pads the gap with `null` (JSON has
 * no `undefined`), matching how the value would round-trip through `JSON.stringify`.
 */
export function setAt<T>(root: T, path: JsonPath, value: unknown): T {
  if (path.length === 0) return value as T;
  const [head, ...rest] = path;
  if (typeof head === "number") {
    const source = Array.isArray(root) ? root : [];
    const next = source.slice();
    while (next.length < head) next.push(null);
    next[head] = rest.length ? setAt(next[head], rest, value) : value;
    return next as unknown as T;
  }
  const source = root && typeof root === "object" && !Array.isArray(root) ? (root as Record<string, unknown>) : {};
  const next: Record<string, unknown> = { ...source };
  next[head] = rest.length ? setAt(next[head], rest, value) : value;
  return next as unknown as T;
}

/**
 * Returns a new root with the key or index at `path` removed — an object loses that key
 * entirely (not set to `null`), an array splices that index out and shifts later elements
 * down. A path that does not resolve to anything present is a no-op that returns `root`
 * unchanged (by reference), so a caller can compare before/after to detect a real edit.
 * The empty path (the document root itself) has nothing to remove it from and is also a
 * no-op.
 */
export function deleteAt<T>(root: T, path: JsonPath): T {
  if (path.length === 0) return root;
  if (path.length === 1) {
    const [key] = path;
    if (Array.isArray(root)) {
      if (typeof key !== "number" || key < 0 || key >= root.length) return root;
      const next = root.slice();
      next.splice(key, 1);
      return next as unknown as T;
    }
    if (root && typeof root === "object") {
      const record = root as Record<string, unknown>;
      if (typeof key !== "string" || !Object.prototype.hasOwnProperty.call(record, key)) return root;
      const next = { ...record };
      delete next[key];
      return next as unknown as T;
    }
    return root;
  }
  const [head, ...rest] = path;
  const child = getAt(root, [head]);
  if (child === undefined) return root;
  const updatedChild = deleteAt(child, rest);
  if (updatedChild === child) return root;
  return setAt(root, [head], updatedChild);
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
 * indistinguishable from an array index without the document to disambiguate against, which
 * is not a concern this codebase's own paths run into (a keyed cell's own `key` field is
 * document DATA, never itself a path segment addressing the cells array).
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

/**
 * Renders `path` the way the engine spells a document address in its own Parse errors:
 * dotted keys with bracketed indices, e.g. `["state","world",3,"cells",0,"key"]` ->
 * `"state.world[3].cells[0].key"`.
 */
export function pathToEnginePath(path: JsonPath): string {
  let result = "";
  for (const segment of path) {
    if (typeof segment === "number") result += `[${segment}]`;
    else result += result.length ? `.${segment}` : segment;
  }
  return result;
}
