/**
 * Shared type definitions for the studio machine: context, events, and the single boot seam
 * (`BootEngine`) the machine's boot actor calls. Split out of `studioMachine.ts` so the region
 * modules (`document.ts`, `preview.ts`, `geometry.ts`) and the machine setup itself share one
 * definition of "what the context looks like" instead of re-declaring it.
 */
import type { JsonPath } from "../../document/jsonPath";
import type { DocumentRole } from "../../document/documentRole";
import type {
  EngineCell,
  EngineDiagnostic,
  JudgeTrace,
  RowInfo,
  WorldEngine,
} from "../../native/engineTypes";
import type { FetchLike, OfficialLoad } from "../../official/officialClient";
import type { ByteStore } from "../../official/byteStore";
import type { ResolvedOfficial } from "../../official/officialBase";
import type { LocalDraftStore } from "../../document/localDrafts";

/**
 * The one seam the machine's boot actor calls to turn a verified official load into a live
 * engine session. In production this IS `bootEngineFromOfficial` from
 * `native/engineBoot.ts` (owned by the boot work package, built in parallel) — the machine
 * takes it as `input.bootEngine` rather than importing that module directly, so this file
 * compiles and tests today whether or not that module has landed in this worktree yet. A test
 * (or, once that module exists, whoever constructs the actor for real — see the boot package's own
 * README) supplies the concrete function; its signature is exactly `bootEngineFromOfficial`'s own.
 */
export type BootEngine = (official: OfficialLoad, options: EngineBootOptionsLike) => Promise<WorldEngine>;

/** Mirrors `native/engineBoot.ts`'s own `EngineBootOptions` shape (mode + optional fetch) without
 * importing a module this package does not own. */
export interface EngineBootOptionsLike {
  readonly mode: "inline" | "worker";
  readonly fetchImpl?: FetchLike;
}

export interface StudioMachineInput {
  readonly official: ResolvedOfficial;
  readonly engineMode: "inline" | "worker";
  readonly fetchImpl?: FetchLike;
  readonly byteStore?: ByteStore;
  readonly draftStore?: LocalDraftStore;
  readonly bootEngine: BootEngine;
}

export type { LocalDraftStore };
export type OfficialSourceLike = OfficialLoad["source"];

export interface BootState {
  readonly status: "booting" | "ready" | "refused";
  readonly refusal: string | null;
  readonly build: { readonly commit: string; readonly schemaVersion: string; readonly source: OfficialSourceLike } | null;
}

/** A prior document snapshot on the undo/redo stacks — the document state MINUS its own
 * diagnostics/deferred/composed, which are re-derived by validation rather than carried through
 * history (a document worth undoing back to is always re-validated, never trusted stale). */
export interface DocumentRevision {
  readonly text: string;
  readonly value: unknown;
  readonly label: string;
}

export interface DocumentState extends DocumentRevision {
  readonly name: string;
  readonly role: DocumentRole;
  readonly revision: number;
  readonly cleanRevision: number;
  readonly past: readonly DocumentRevision[];
  readonly future: readonly DocumentRevision[];
  readonly diagnostics: readonly EngineDiagnostic[];
  readonly deferred: readonly string[];
  /** The engine's composed standalone JSON for a fragment or the island root; null for a
   * standalone world document (validated by `engine.parse` alone) or before validation runs. */
  readonly composed: string | null;
}

export type PreviewScriptStep =
  | { readonly kind: "write"; readonly row: string; readonly key?: string; readonly value: bigint; readonly write: "set" | "add" }
  | { readonly kind: "tick"; readonly tick: bigint };

export interface PreviewSnapshot {
  readonly tick: bigint;
  readonly rows: readonly RowInfo[];
  readonly hash: string;
  readonly trace: JudgeTrace | null;
  /** How many `PreviewScriptStep`s had already run when this snapshot was taken — replaying
   * `script.slice(0, scriptLength)` against a fresh compile reproduces this exact state. */
  readonly scriptLength: number;
}

export interface PreviewState {
  readonly handle: string | null;
  readonly tick: bigint;
  readonly rows: readonly RowInfo[];
  readonly snapshots: readonly PreviewSnapshot[];
  readonly cursor: number;
  readonly refusals: readonly string[];
  readonly status: "idle" | "compiling" | "ready" | "refused";
  readonly refusal: string | null;
  /** The full write/tick recipe since the last compile — the source `JUMP_TO_TICK`/undo/redo
   * replay against a freshly recompiled handle (the engine has no snapshot-restore of its own). */
  readonly script: readonly PreviewScriptStep[];
  /** The `document.revision` this preview was compiled from — a later document revision makes
   * this preview stale (see the machine's own document-invalidates-preview transition). */
  readonly sourceRevision: number;
}

export interface SelectionState {
  readonly topology: string | null;
  readonly ordinals: readonly number[];
  readonly hovered: number | null;
}

export interface StudioContext {
  readonly official: OfficialLoad | null;
  readonly engine: WorldEngine | null;
  readonly boot: BootState;
  readonly document: DocumentState;
  readonly geometry: Readonly<Record<string, readonly EngineCell[]>>;
  readonly selection: SelectionState;
  readonly preview: PreviewState;
  /** The machine's own construction input, carried into context because an invoked actor's
   * `input` factory only ever sees `{context, event}` — never the original `createActor` input —
   * so anything an actor (the boot actor, the draft actors) needs from it must live here.
   * `draftStore` is resolved to a concrete store at context-construction time (defaulting to
   * `defaultLocalDraftStore`) so nothing downstream repeats that fallback. Not part of the
   * machine's own public "what does the studio look like" contract — a UI selector has no reason
   * to read it. */
  readonly machineInput: StudioMachineInput & { readonly draftStore: LocalDraftStore };
}

export type StudioEvent =
  | { type: "OPEN_OFFICIAL"; name: string }
  | { type: "OPEN_TEXT"; text: string; name?: string }
  | { type: "EDIT_DOCUMENT"; path: JsonPath; value?: unknown; label: string }
  | { type: "APPLY_TEXT"; text: string }
  | { type: "SET_TEXT_DRAFT"; text: string }
  | { type: "UNDO" }
  | { type: "REDO" }
  | { type: "PAINT_CELLS"; topology: string; row: string; ordinals: number[]; value: bigint }
  | { type: "SAVE_DRAFT"; id?: string; title?: string }
  | { type: "LOAD_DRAFT"; id: string }
  | { type: "DELETE_DRAFT"; id: string }
  | { type: "SELECT_TOPOLOGY"; name: string }
  | { type: "SELECT_CELLS"; ordinals: number[]; mode: "replace" | "toggle" }
  | { type: "HOVER_CELL"; ordinal: number | null }
  | { type: "PREVIEW_START" }
  | { type: "PREVIEW_WRITE"; row: string; key?: string; value: bigint; write: "set" | "add" }
  | { type: "PREVIEW_TICK" }
  | { type: "PREVIEW_UNDO" }
  | { type: "PREVIEW_REDO" }
  | { type: "JUMP_TO_TICK"; index: number }
  | { type: "RESET_WORLD" }
  | { type: "PREVIEW_STOP" };

/** Event types that change `context.document` — dispatched to the preview region too (parallel
 * states see every event), so it can stop a preview that would otherwise run against a document
 * the engine hasn't validated yet. `SET_TEXT_DRAFT` is deliberately absent: typing is not a
 * revision. */
export const DOCUMENT_MUTATING_EVENTS: ReadonlySet<StudioEvent["type"]> = new Set([
  "OPEN_OFFICIAL",
  "OPEN_TEXT",
  "EDIT_DOCUMENT",
  "APPLY_TEXT",
  "UNDO",
  "REDO",
  "PAINT_CELLS",
  "LOAD_DRAFT",
]);
