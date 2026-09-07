/**
 * The document region's pure edit reducers and its one async validation step. Kept apart from
 * `studioMachine.ts` so the edit/undo/redo arithmetic (easy to get off-by-one) and the
 * parse-vs-composeTree routing are each testable without an actor around them.
 */
import { deleteAt, getAt, setAt, type JsonPath } from "../../document/jsonPath";
import { checkDocument } from "../../document/intake";
import { parseDocumentText, serializeDocumentText } from "../../document/jsonText";
import { hasComposition, ISLAND_ROOT_DOCUMENT_NAME, readDocumentRole, type DocumentRole } from "../../document/documentRole";
import type { EngineDiagnostic, WorldEngine } from "../../native/engineTypes";
import type { OfficialLoad } from "../../official/officialClient";
import type { DocumentRevision, DocumentState } from "./types";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function requireAppliedDraft(document: DocumentState): void {
  if (document.text !== document.appliedText) {
    throw new Error("Apply or discard the JSON draft before editing other fields or document history.");
  }
}

export function createEmptyDocument(): DocumentState {
  return {
    name: "untitled",
    role: "basis",
    text: "{}",
    value: {},
    label: "New document",
    revision: 0,
    appliedText: "{}",
    savedText: "{}",
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

function freshRevision(document: DocumentState, revision: DocumentRevision, role: DocumentRole): DocumentState {
  if (revision.text === document.appliedText) {
    return document.text === revision.text ? document : {
      ...document, text: revision.text, diagnostics: [], validation: "pending",
    };
  }
  return {
    ...document,
    ...revision,
    appliedText: revision.text,
    role,
    revision: document.revision + 1,
    past: [...document.past, { text: document.appliedText, value: document.value, label: document.label }],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

/** `OPEN_OFFICIAL`: the manifest already names this document's role authoritatively — see
 * `documentRole.ts`'s own remarks on why content alone cannot always reproduce it (a shard). */
export function openOfficialDocument(name: string, text: string, role: DocumentRole): DocumentState {
  return {
    name,
    role,
    text,
    value: parseDocumentText(text),
    label: `Opened ${name}`,
    revision: 0,
    appliedText: text,
    savedText: text,
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

/** `OPEN_TEXT`: a pasted or imported document — runs `checkDocument` first (throws `IntakeRefusal`
 * by name; the caller decides how a refusal surfaces). */
export function openTextDocument(text: string, name: string | undefined): DocumentState {
  checkDocument(text);
  const value: unknown = parseDocumentText(text);
  return {
    name: name ?? "untitled",
    role: readDocumentRole(value),
    text,
    value,
    label: "Opened document",
    revision: 0,
    appliedText: text,
    savedText: text,
    past: [],
    future: [],
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

/** Local drafts can contain unfinished JSON; retain that text for repair in the editor. */
export function openDraftDocument(text: string, name: string): DocumentState {
  checkDocument(text);
  try {
    return openTextDocument(text, name);
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return {
      ...createEmptyDocument(), name, text, savedText: text,
      label: "Loaded unfinished JSON draft",
      validation: "refused",
      diagnostics: [{ path: "", message: "This saved draft contains unfinished JSON. Repair and apply it in the JSON tab." }],
    };
  }
}

/** `APPLY_TEXT`: the JSON editor's Apply — intake-checks, parses, becomes a revision. */
export function applyText(document: DocumentState, text: string): DocumentState {
  checkDocument(text);
  const value: unknown = parseDocumentText(text);
  return freshRevision(document, { text, value, label: "Apply JSON" }, readDocumentRole(value));
}

/** `EDIT_DOCUMENT`: `value === undefined` deletes whatever is at `path` (see `SchemaNode.tsx`'s
 * own `DocumentEdit` contract, which this mirrors exactly). */
export function editDocument(document: DocumentState, path: JsonPath, value: unknown, label: string): DocumentState {
  requireAppliedDraft(document);
  const nextValue = value === undefined ? deleteAt(document.value, path) : setAt(document.value, path, value);
  const text = serializeDocumentText(nextValue);
  return freshRevision(document, { text, value: nextValue, label }, readDocumentRole(nextValue));
}

/** `PAINT_CELLS`: writes `cells[]` entries (string keys) on the named `state.world` row, as one
 * revision, through the same `setAt` edit path as `EDIT_DOCUMENT`. The authored `value` is kept as
 * the `bigint` it already is — `serializeDocumentText` writes it back out as a bare integer
 * literal regardless of magnitude, so there is no separate number/string encoding step to get
 * wrong (see `document/jsonText.ts`'s own remarks; a JSON.parse/JSON.stringify round trip through
 * an UNTOUCHED sentinel elsewhere in the same document — see `tests/studioMachine.test.cjs`'s own
 * Int64 fidelity coverage — is what this guards, not this cell's own write). */
export function paintCells(
  document: DocumentState,
  topology: string,
  row: string,
  ordinals: readonly number[],
  value: bigint,
): DocumentState {
  requireAppliedDraft(document);
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
  for (const ordinal of ordinals) {
    const key = String(ordinal);
    byKey.set(key, { ...(byKey.get(key) ?? {}), key, value });
  }
  const cells = [...byKey.values()];
  const nextValue = setAt(document.value, ["state", "world", rowIndex, "cells"], cells);
  const text = serializeDocumentText(nextValue);
  const label = `paint ${ordinals.length} cells of ${topology}.${row}`;
  return freshRevision(document, { text, value: nextValue, label }, readDocumentRole(nextValue));
}

export function undoDocument(document: DocumentState): DocumentState {
  requireAppliedDraft(document);
  if (document.past.length === 0) {
    return document;
  }
  const previous = document.past[document.past.length - 1];
  return {
    ...document,
    ...previous,
    appliedText: previous.text,
    role: readDocumentRole(previous.value),
    revision: document.revision + 1,
    past: document.past.slice(0, -1),
    future: [{ text: document.appliedText, value: document.value, label: document.label }, ...document.future],
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

export function redoDocument(document: DocumentState): DocumentState {
  requireAppliedDraft(document);
  if (document.future.length === 0) {
    return document;
  }
  const [next, ...rest] = document.future;
  return {
    ...document,
    ...next,
    appliedText: next.text,
    role: readDocumentRole(next.value),
    revision: document.revision + 1,
    past: [...document.past, { text: document.appliedText, value: document.value, label: document.label }],
    future: rest,
    diagnostics: [],
    deferred: [],
    composed: null,
    validation: "pending",
  };
}

export interface ValidationOutcome {
  readonly ok: boolean;
  readonly diagnostics: readonly EngineDiagnostic[];
  readonly deferred: readonly string[];
  readonly composed: string | null;
}

/** One `official` load's own `documents` map (every `composeTree` call needs it in full, since a
 * fragment now composes over the WHOLE island — see `validateDocument`'s own remarks), built at
 * most once per `OfficialLoad` and reused by every later validation. `LazyOfficialSet.get` already
 * memoizes per name, but re-awaiting ~30 of them and rebuilding the record on every keystroke is
 * still real (if small) per-call overhead this keys away entirely; the `WeakMap` key is the load
 * object itself, so a fresh boot (a fresh `OfficialLoad`) starts a fresh cache with no invalidation
 * to get wrong. */
const composeTreeDocumentsCache = new WeakMap<OfficialLoad, Promise<Record<string, string>>>();

/** Every official document of role world/basis/fragment/shard, keyed by its manifest name — see
 * the machine's own remarks on why `composed` entries (already-composed roots) are never part of
 * this map. */
function loadComposeTreeDocuments(official: OfficialLoad): Promise<Record<string, string>> {
  const cached = composeTreeDocumentsCache.get(official);
  if (cached) {
    return cached;
  }
  const loaded = (async () => {
    const documents: Record<string, string> = {};
    for (const entry of official.manifest.documents) {
      documents[entry.name] = await official.documents.get(entry.name);
    }
    return documents;
  })();
  composeTreeDocumentsCache.set(official, loaded);
  return loaded;
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
 *    else, never composed alone) composes over the ISLAND ITSELF: `composeTree(
 *    ISLAND_ROOT_DOCUMENT_NAME, documents, edited)`, exactly as opening the real game would.
 *
 * A bare fragment used to compose over a minimal synthetic root (`standard.basis.json` plus one
 * import) instead, for speed — but most shipped fragments (billiards, bowling, poker, chess, …)
 * refuse under that bare basis BY DESIGN: they name the island's own body/look rows and host
 * registers, which only the real island supplies. That made the synthetic root wrong, not fast —
 * every such fragment read as permanently broken. Validation therefore composes against the
 * island after the machine's edit debounce. The browser worker keeps that work off the UI
 * thread; preview compilation remains a separate explicit action.
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

  const rootName = (hasComposition(document.value) || document.role === "basis")
    ? document.name
    : ISLAND_ROOT_DOCUMENT_NAME;
  return composeOutcome(await engine.composeTree(rootName, documents, edited));
}
