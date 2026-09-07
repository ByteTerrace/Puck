/**
 * The only document validation kept in TypeScript: a byte-size cap, and a refusal for an integer
 * literal `JSON.parse` would round away. Everything else — schema shape, vocabulary, cross-field
 * rules — is the native engine's job (`Puck.World.Browser`'s `Parse`/`Compile`), never
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

const MAX_SAFE = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_SAFE = BigInt(Number.MIN_SAFE_INTEGER);

function isDigit(ch: string | undefined): boolean {
  return ch !== undefined && ch >= "0" && ch <= "9";
}

function isJsonWhitespace(ch: string | undefined): boolean {
  return ch === " " || ch === "\t" || ch === "\n" || ch === "\r";
}

/**
 * Walks `text` as JSON, refusing the first integer literal (no fraction, no exponent) whose
 * magnitude `JSON.parse` cannot preserve exactly — before parsing ever gets the chance to round
 * it. Runs its own minimal recursive-descent scan rather than post-inspecting `JSON.parse`'s
 * output, because by then the precision is already lost and the offending source offset is gone.
 *
 * Any *other* malformed-JSON condition this scan notices is swallowed rather than reported here —
 * `JSON.parse` remains the one authority on JSON syntax; this function's only contract is the
 * unsafe-integer-literal refusal.
 */
/** Thrown internally to abandon the scan on any JSON shape it does not recognize; never escapes. */
class StructuralStop extends Error {}

export function scanUnsafeIntegerLiterals(text: string): void {
  const length = text.length;
  let index = 0;

  function skipWhitespace(): void {
    while (index < length && isJsonWhitespace(text[index])) {
      index += 1;
    }
  }

  function parseString(): void {
    // text[index] === '"'
    index += 1;
    while (index < length) {
      const ch = text[index];
      if (ch === "\\") {
        index += 2;
        continue;
      }
      if (ch === '"') {
        index += 1;
        return;
      }
      index += 1;
    }
    throw new StructuralStop();
  }

  function parseNumber(path: string): void {
    const start = index;
    if (text[index] === "-") {
      index += 1;
    }
    while (isDigit(text[index])) {
      index += 1;
    }
    let isInteger = true;
    if (text[index] === ".") {
      isInteger = false;
      index += 1;
      while (isDigit(text[index])) {
        index += 1;
      }
    }
    if (text[index] === "e" || text[index] === "E") {
      isInteger = false;
      index += 1;
      if (text[index] === "+" || text[index] === "-") {
        index += 1;
      }
      while (isDigit(text[index])) {
        index += 1;
      }
    }

    const literal = text.slice(start, index);
    if (isInteger && literal !== "" && literal !== "-") {
      const value = BigInt(literal);
      if (value > MAX_SAFE || value < MIN_SAFE) {
        throw new IntakeRefusal(
          `integer literal '${literal}' at ${path} cannot be preserved exactly (outside Number.MAX_SAFE_INTEGER).`,
          path,
          start,
        );
      }
    }
  }

  function parseValue(path: string): void {
    skipWhitespace();
    const ch = text[index];
    if (ch === "{") {
      parseObject(path);
    } else if (ch === "[") {
      parseArray(path);
    } else if (ch === '"') {
      parseString();
    } else if (ch === "t" || ch === "f" || ch === "n") {
      // true / false / null — no literal value worth validating; skip the run of letters.
      while (index < length && /[a-z]/.test(text[index])) {
        index += 1;
      }
    } else if (ch === "-" || isDigit(ch)) {
      parseNumber(path);
    } else {
      throw new StructuralStop();
    }
  }

  function parseObject(path: string): void {
    index += 1; // '{'
    skipWhitespace();
    if (text[index] === "}") {
      index += 1;
      return;
    }
    for (;;) {
      skipWhitespace();
      if (text[index] !== '"') {
        throw new StructuralStop();
      }
      const keyStart = index + 1;
      parseString();
      const key = text.slice(keyStart, index - 1);
      skipWhitespace();
      if (text[index] !== ":") {
        throw new StructuralStop();
      }
      index += 1;
      parseValue(`${path}.${key}`);
      skipWhitespace();
      if (text[index] === ",") {
        index += 1;
        continue;
      }
      if (text[index] === "}") {
        index += 1;
        return;
      }
      throw new StructuralStop();
    }
  }

  function parseArray(path: string): void {
    index += 1; // '['
    skipWhitespace();
    if (text[index] === "]") {
      index += 1;
      return;
    }
    let arrayIndex = 0;
    for (;;) {
      parseValue(`${path}[${arrayIndex}]`);
      arrayIndex += 1;
      skipWhitespace();
      if (text[index] === ",") {
        index += 1;
        continue;
      }
      if (text[index] === "]") {
        index += 1;
        return;
      }
      throw new StructuralStop();
    }
  }

  try {
    parseValue("$");
  } catch (error) {
    if (error instanceof IntakeRefusal) {
      throw error;
    }
    // Any other shape this scan cannot make sense of is JSON.parse's problem to report, not ours.
  }
}

/** Runs every TypeScript-side intake check, in cap-then-literal-scan order. */
export function checkDocument(text: string): void {
  checkDocumentSize(text);
  scanUnsafeIntegerLiterals(text);
}
