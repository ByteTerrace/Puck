/**
 * The only document validation kept in TypeScript: a byte-size cap. An integer literal outside
 * `Number.MAX_SAFE_INTEGER` is no longer refused here — `document/jsonText.ts`'s
 * `parseDocumentText`/`serializeDocumentText` preserve one exactly as a `bigint` leaf, so there is
 * nothing left for this module to protect against. Everything else — schema shape, vocabulary,
 * cross-field rules — is the native engine's job (`Puck.World.Browser`'s `Parse`/`Compile`), never
 * re-implemented here.
 */

export const MAX_DOCUMENT_BYTES = 2 * 1024 * 1024;

export class IntakeRefusal extends Error {
  /** A JSON-path-like locator ("$.state.world[2].value"), or "" when the refusal has no single path. */
  readonly path: string;
  /** The character offset into the original text the refusal points at. */
  readonly offset: number;

  constructor(message: string, path: string, offset: number) {
    super(message);
    this.name = "IntakeRefusal";
    this.path = path;
    this.offset = offset;
  }
}

/** Refuses by name when `text`'s UTF-8 encoding exceeds the 2 MB cap. */
export function checkDocumentSize(text: string): void {
  const byteLength = new TextEncoder().encode(text).length;
  if (byteLength > MAX_DOCUMENT_BYTES) {
    throw new IntakeRefusal(`document is ${byteLength} bytes, over the ${MAX_DOCUMENT_BYTES} byte cap.`, "", 0);
  }
}

/** Runs every TypeScript-side intake check — today, just the size cap. */
export function checkDocument(text: string): void {
  checkDocumentSize(text);
}
