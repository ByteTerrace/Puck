/**
 * Int64-faithful JSON text <-> document value round trip. `JSON.parse`/`JSON.stringify` alone
 * round an integer literal outside `[Number.MIN_SAFE_INTEGER, Number.MAX_SAFE_INTEGER]` to the
 * nearest representable double — the exact corruption `games/tictactoe.world.json`'s own
 * `state.world[].min`/`max` sentinels (Int64.MinValue/MaxValue) hit on every prior
 * EDIT_DOCUMENT/PAINT_CELLS round trip. This module is the one place a document's text and its
 * in-memory value cross that boundary for the studio: an out-of-range integer literal survives
 * {@link parseDocumentText} as a `bigint` leaf, and a `bigint` leaf serializes back out through
 * {@link serializeDocumentText} as the identical bare integer literal — never a quoted string,
 * never rounded.
 *
 * Both directions rest on two runtime features neither carry a compile-time type yet in the
 * TypeScript lib this repository ships (6.0.3, checked when this module was written): `JSON.parse`
 * passing its reviver a third `context` argument carrying the literal's own source text (the
 * "JSON.parse source access" proposal), and `JSON.rawJSON`, which lets a `JSON.stringify` replacer
 * emit a bare, unquoted numeric literal instead of a coerced JSON value. Both ship in Node 21+ and
 * every current evergreen browser — verified against the Node build this repository targets, see
 * {@link hasJsonSourceTextSupport}'s own remarks — so the ambient `JSON` augmentation below only
 * fills a lib-typing gap, never a runtime one.
 */

declare global {
  interface JSON {
    // TypeScript's shipped lib.es5.d.ts (6.0.3, checked here) has not yet added the reviver's
    // third "source" argument, though every runtime this repository targets implements it. This
    // is an ADDITIONAL overload, not a replacement — every other call site's own two-argument
    // `JSON.parse(text, reviver)` keeps resolving to the lib's own signature untouched.
    parse(text: string, reviver: (this: unknown, key: string, value: unknown, context: { readonly source: string }) => unknown): unknown;
    /** Wraps `value` (already-valid JSON number text, or a number/bigint whose `toString()` is)
     * so a `JSON.stringify` replacer returning it emits that exact text verbatim, unquoted —
     * TypeScript's shipped lib does not declare this member yet either. */
    rawJSON(value: number | string | bigint): unknown;
  }
}

/** A named refusal, never a silent rounding: thrown when this runtime's `JSON.parse` does not
 * pass the reviver a source-text `context` (or lacks `JSON.rawJSON`) — an Int64 authoring literal
 * cannot be preserved exactly here, so parsing/serializing refuses outright. */
export class JsonSourceTextUnsupportedError extends Error {
  constructor() {
    super(
      "this runtime's JSON.parse does not pass the reviver a source-text context (or lacks " +
      "JSON.rawJSON) — an Int64 authoring literal cannot be preserved exactly, so document " +
      "text cannot be parsed or serialized here rather than silently rounding one.",
    );
    this.name = "JsonSourceTextUnsupportedError";
  }
}

const INTEGER_LITERAL = /^-?\d+$/;
const MAX_SAFE_BIGINT = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_SAFE_BIGINT = BigInt(Number.MIN_SAFE_INTEGER);

let supported: boolean | null = null;

/**
 * Probes (once, memoized) whether this runtime's `JSON.parse` exposes the reviver's source-text
 * `context` and whether `JSON.rawJSON` exists. Exported so a caller — or a test — can assert the
 * runtime this repository actually ships against carries the feature, rather than trusting the
 * ambient type augmentation above (a compile-time type asserts nothing about the runtime under it).
 */
export function hasJsonSourceTextSupport(): boolean {
  if (supported === null) {
    let sawSource = false;
    JSON.parse("0", (_key, value, context) => {
      if (context && typeof context.source === "string") sawSource = true;
      return value;
    });
    supported = sawSource && typeof JSON.rawJSON === "function";
  }
  return supported;
}

function requireSupport(): void {
  if (!hasJsonSourceTextSupport()) throw new JsonSourceTextUnsupportedError();
}

/**
 * Parses `text`; every plain decimal integer literal (no fraction, no exponent — the same shapes
 * `document/intake.ts`'s retired unsafe-integer scan used to refuse) whose magnitude falls outside
 * `Number.MAX_SAFE_INTEGER`/`MIN_SAFE_INTEGER` survives as a `bigint` leaf instead of a rounded
 * `number`. A fraction or an exponent is never affected — floats carry no such fidelity contract.
 */
export function parseDocumentText(text: string): unknown {
  requireSupport();
  return JSON.parse(text, (_key, value, context) => {
    if (typeof value === "number" && INTEGER_LITERAL.test(context.source)) {
      const big = BigInt(context.source);
      if (big > MAX_SAFE_BIGINT || big < MIN_SAFE_BIGINT) return big;
    }
    return value;
  });
}

/**
 * Serializes `value`, 2-space indented; every `bigint` leaf — whether left by a prior
 * {@link parseDocumentText} or written fresh by an edit — serializes back out as the identical
 * bare integer literal, never a quoted string.
 */
export function serializeDocumentText(value: unknown): string {
  requireSupport();
  return JSON.stringify(value, (_key, v) => (typeof v === "bigint" ? JSON.rawJSON(v.toString()) : v), 2);
}
