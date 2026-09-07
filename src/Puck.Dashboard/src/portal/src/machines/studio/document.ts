/**
 * The document region's pure edit reducers and its one async validation step. Kept apart from
 * `studioMachine.ts` so the edit/undo/redo arithmetic (easy to get off-by-one) and the
 * parse-vs-composeTree routing are each testable without an actor around them.
 */
import { deleteAt, getAt, setAt, type JsonPath } from "../../document/jsonPath";
import { checkDocument } from "../../document/intake";
import { hasComposition, readDocumentRole, type DocumentRole } from "../../document/documentRole";
import { REQUIRED_WORLD_SCHEMA } from "../../official/manifest";
import type { EngineDiagnostic, WorldEngine } from "../../native/engineTypes";
import type { OfficialLoad } from "../../official/officialClient";
import type { DocumentRevision, DocumentState } from "./types";

/** `OfficialWorldDocumentScanner`'s own `BasisDocumentName` — the one document every bare fragment
 * composes over when it is validated on its own (see `validateDocument`'s own remarks). */
const STANDARD_BASIS_NAME = "standard.basis.json";

const MAX_SAFE_BIGINT = BigInt(Number.MAX_SAFE_INTEGER);
const MIN_SAFE_BIGINT = BigInt(Number.MIN_SAFE_INTEGER);

/** Encodes a 64-bit authoring value into the JSON shapes a state row's cell value accepts
 * (`number | string | boolean` — see `worldDefinition.generated.ts`'s `state.world[].cells[].value`):
 * a plain JSON number when it round-trips exactly, otherwise its exact decimal string — the same
 * escape hatch the schema itself carries for a value `JSON.parse` cannot represent (see
 * `tests/engine-wasm.test.cjs`'s own remarks on tictactoe.world.json's Int64.Min/MaxValue sentinels). */
export function encodeCellValue(value: bigint): number | string {
  return (value <= MAX_SAFE_BIGINT && value >= MIN_SAFE_BIGINT) ? Number(value) : value.toString();
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function createEmptyDocument(): DocumentState {
  return {
    name: "untitled",
    role: "basis",
    text: "{}",
    value: {},
    label: "New document",
    revision: 0,
    cleanRevision: 0,
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

function freshRevision(document: DocumentState, revision: DocumentRevision, role: DocumentRole): DocumentState {
  if (revision.text === document.text) {
    return document;
  }
  return {
    ...document,
    ...revision,
    role,
    revision: document.revision + 1,
    past: [...document.past, { text: document.text, value: document.value, label: document.label }],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

/** `OPEN_OFFICIAL`: the manifest already names this document's role authoritatively — see
 * `documentRole.ts`'s own remarks on why content alone cannot always reproduce it (a shard). */
export function openOfficialDocument(name: string, text: string, role: DocumentRole): DocumentState {
  return {
    name,
    role,
    text,
    value: JSON.parse(text),
    label: `Opened ${name}`,
    revision: 0,
    cleanRevision: 0,
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

/** `OPEN_TEXT`: a pasted or imported document — runs `checkDocument` first (throws `IntakeRefusal`
 * by name; the caller decides how a refusal surfaces). */
export function openTextDocument(text: string, name: string | undefined): DocumentState {
  checkDocument(text);
  const value: unknown = JSON.parse(text);
  return {
    name: name ?? "untitled",
    role: readDocumentRole(value),
    text,
    value,
    label: "Opened document",
    revision: 0,
    cleanRevision: 0,
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

/** `APPLY_TEXT`: the JSON editor's Apply — intake-checks, parses, becomes a revision. */
export function applyText(document: DocumentState, text: string): DocumentState {
  checkDocument(text);
  const value: unknown = JSON.parse(text);
  return freshRevision(document, { text, value, label: "Apply JSON" }, readDocumentRole(value));
}

/** `EDIT_DOCUMENT`: `value === undefined` deletes whatever is at `path` (see `SchemaNode.tsx`'s
 * own `DocumentEdit` contract, which this mirrors exactly). */
export function editDocument(document: DocumentState, path: JsonPath, value: unknown, label: string): DocumentState {
  const nextValue = value === undefined ? deleteAt(document.value, path) : setAt(document.value, path, value);
  const text = JSON.stringify(nextValue, null, 2);
  return freshRevision(document, { text, value: nextValue, label }, readDocumentRole(nextValue));
}

/** `PAINT_CELLS`: writes `cells[]` entries (string keys) on the named `state.world` row, as one
 * revision, through the same `setAt` edit path as `EDIT_DOCUMENT`. */
export function paintCells(
  document: DocumentState,
  topology: string,
  row: string,
  ordinals: readonly number[],
  value: bigint,
): DocumentState {
  const rows = getAt(document.value, ["state", "world"]);
  if (!Array.isArray(rows)) {
    throw new Error(`paint: document has no state.world rows (looking for '${row}').`);
  }
  const rowIndex = rows.findIndex((candidate) => isRecord(candidate) && candidate.name === row);
  if (rowIndex < 0) {
    throw new Error(`paint: no state.world row named '${row}'.`);
  }
  const existingCells = (rows[rowIndex] as Record<string, unknown>).cells;
  const byKey = new Map<string, Record<string, unknown>>(
    Array.isArray(existingCells)
      ? existingCells.filter(isRecord).map((cell) => [String(cell.key), cell])
      : [],
  );
  const encoded = encodeCellValue(value);
  for (const ordinal of ordinals) {
    const key = String(ordinal);
    byKey.set(key, { ...(byKey.get(key) ?? {}), key, value: encoded });
  }
  const cells = [...byKey.values()];
  const nextValue = setAt(document.value, ["state", "world", rowIndex, "cells"], cells);
  const text = JSON.stringify(nextValue, null, 2);
  const label = `paint ${ordinals.length} cells of ${topology}.${row}`;
  return freshRevision(document, { text, value: nextValue, label }, readDocumentRole(nextValue));
}

export function undoDocument(document: DocumentState): DocumentState {
  if (document.past.length === 0) {
    return document;
  }
  const previous = document.past[document.past.length - 1];
  return {
    ...document,
    ...previous,
    role: readDocumentRole(previous.value),
    revision: document.revision + 1,
    past: document.past.slice(0, -1),
    future: [{ text: document.text, value: document.value, label: document.label }, ...document.future],
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

export function redoDocument(document: DocumentState): DocumentState {
  if (document.future.length === 0) {
    return document;
  }
  const [next, ...rest] = document.future;
  return {
    ...document,
    ...next,
    role: readDocumentRole(next.value),
    revision: document.revision + 1,
    past: [...document.past, { text: document.text, value: document.value, label: document.label }],
    future: rest,
    diagnostics: [],
    deferred: [],
    composed: null,
  };
}

export interface ValidationOutcome {
  readonly ok: boolean;
  readonly diagnostics: readonly EngineDiagnostic[];
  readonly deferred: readonly string[];
  readonly composed: string | null;
}

/** Builds the `documents` map every `composeTree` call needs: every official document of role
 * world/basis/fragment/shard, keyed by its manifest name — see the machine's own remarks on why
 * `composed` entries (already-composed roots) are never part of this map. `LazyOfficialSet.get`
 * memoizes per name, so repeated validations after the first pay only for what changed. */
async function loadComposeTreeDocuments(official: OfficialLoad): Promise<Record<string, string>> {
  const documents: Record<string, string> = {};
  for (const entry of official.manifest.documents) {
    documents[entry.name] = await official.documents.get(entry.name);
  }
  return documents;
}

function composeOutcome(result: Awaited<ReturnType<WorldEngine["composeTree"]>>): ValidationOutcome {
  return (result.ok
    ? { ok: true, diagnostics: [], deferred: result.deferred, composed: result.composed ?? null }
    : { ok: false, diagnostics: result.errors, deferred: result.deferred, composed: null }
  );
}

/**
 * Validates `document` against the live engine:
 *
 *  - a standalone world document (role 'world', no `basis`/`imports`) goes through `engine.parse`
 *    alone;
 *  - a document that already carries its own `basis`/`imports` — the island root itself
 *    (`puck.world.json`), or a shard (every shard's own `basis` points back at it) — composes as
 *    ITS OWN root: `composeTree(document.name, documents, edited)`, exactly as opening it for
 *    real would;
 *  - a bare fragment (no `basis`/`imports` of its own — it exists to be imported BY something
 *    else, never composed alone) is wrapped in a minimal synthetic root over
 *    `standard.basis.json`, the same shape `tests/engine-wasm.test.cjs`'s own
 *    "ComposeTree() with an edited document" scenario builds by hand. Composing a bare fragment
 *    against the REAL island root would still validate it correctly, but at the cost of
 *    compiling the whole MMO island (every district, every module) just to check one fragment —
 *    empirically minutes of engine time for what should be a fast per-edit check — so a fragment
 *    is always validated against the smallest host that can import it.
 */
export async function validateDocument(
  engine: WorldEngine,
  official: OfficialLoad,
  document: Pick<DocumentState, "name" | "role" | "text" | "value">,
): Promise<ValidationOutcome> {
  if (document.role === "world" && !hasComposition(document.value)) {
    const result = await engine.parse(document.text);
    return (result.ok
      ? { ok: true, diagnostics: [], deferred: result.deferred, composed: null }
      : { ok: false, diagnostics: result.errors, deferred: result.deferred, composed: null }
    );
  }

  const documents = await loadComposeTreeDocuments(official);
  const edited = { name: document.name, json: document.text };

  if (hasComposition(document.value) || document.role === "basis") {
    return composeOutcome(await engine.composeTree(document.name, documents, edited));
  }

  const syntheticRootName = "$fragment-preview-root.json";
  const syntheticRootJson = JSON.stringify({
    schema: REQUIRED_WORLD_SCHEMA,
    basis: STANDARD_BASIS_NAME,
    imports: [{ document: document.name, as: null }],
  });
  return composeOutcome(await engine.composeTree(
    syntheticRootName,
    { ...documents, [syntheticRootName]: syntheticRootJson },
    edited,
  ));
}
