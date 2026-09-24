/**
 * Shared type definitions for the studio machine: context, events, and the single boot seam (`BootEngine`) the
 * machine calls once for each engine it owns. Split out of `studioMachine.ts` so the region modules and the machine
 * setup share one definition of what the context looks like.
 */
import type {
  EngineCell,
  JudgeTrace,
  LanguageServerChannel,
  RowInfo,
  SourceCompileResult,
  SourceDiagnostic,
  SourceMap,
  WorldEngine,
} from "../../native/engineTypes";
import type { FetchLike, OfficialLoad } from "../../official/officialClient";
import type { ByteStore } from "../../official/byteStore";
import type { ResolvedOfficial } from "../../official/officialBase";
import type { DocumentRole } from "../../official/manifest";
import type { DraftListing, LocalDraftStore } from "../../document/localDrafts";

/** Injectable boot seam; production supplies native/engineBoot.ts, tests supply a local bundle or a fake. */
export type BootEngine = (official: OfficialLoad, options: EngineBootOptionsLike) => Promise<WorldEngine>;

/** Options shared with the native engine boot implementation. */
export interface EngineBootOptionsLike {
  readonly mode: "inline" | "worker";
  readonly fetchImpl?: FetchLike;
  readonly signal?: AbortSignal;
  /** The language engine's compiled module, which the world engine boots from without compiling its own. */
  readonly wasmModule?: WebAssembly.Module;
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

/** The official build, which loads when the studio starts. Its build line is the manifest's. */
export interface BootState {
  readonly status: "booting" | "ready" | "refused";
  readonly refusal: string | null;
  readonly build: { readonly commit: string; readonly dirty: boolean; readonly schemaVersion: string; readonly source: OfficialSourceLike } | null;
}

/** The language engine, which boots from the official build when the studio starts. A refused one leaves the
 * workspace editable, without diagnostics, and refuses the preview with its reason. */
export interface LanguageEngineState {
  readonly status: "booting" | "ready" | "refused";
  readonly refusal: string | null;
}

/** The world engine, which boots the first time an island check or a preview needs it. */
export interface WorldEngineState {
  readonly status: "dormant" | "booting" | "ready" | "refused";
  readonly refusal: string | null;
}

/** One workspace file. `text` is the editor's current buffer; `savedText` is the text as last opened or saved (the
 * dirty baseline); `officialText` is the official build's text (the draft overlay's baseline). `version` counts the
 * editor's changes to the file since the workspace opened; the language server sees the same numbers. */
export interface WorkspaceFile {
  readonly text: string;
  readonly savedText: string;
  readonly officialText: string;
  readonly version: number;
}

/** The language server's latest diagnostics for one file — the source tier — and the file version they describe. */
export interface FileDiagnostics {
  readonly version: number;
  readonly items: readonly SourceDiagnostic[];
}

/**
 * The world engine's check of one workspace revision. `compile` is the semantic tier: the full compile of the open
 * `.puck` source (every diagnostic, its IR, and its source map), or `null` when the open file has no source. When it
 * has no errors, the workspace's root is composed and validated; `composeDiagnostics` are what that found, joining the
 * semantic tier. `ok` means both passed, counting errors only: an `information` finding never blocks the preview.
 * `value` is the composed document parsed with 64-bit literals kept exact.
 */
export interface IslandCheck {
  readonly revision: number;
  readonly compile: { readonly path: string; readonly result: SourceCompileResult } | null;
  readonly ok: boolean;
  readonly composed: string | null;
  readonly value: unknown;
  readonly composeDiagnostics: readonly SourceDiagnostic[];
}

/** A source span the editor should show and select; `nonce` makes a repeated reveal of the same span a new one. */
export interface SourceReveal {
  readonly path: string;
  readonly line: number;
  readonly column: number;
  readonly length: number;
  readonly nonce: number;
}

export interface WorkspaceState {
  /** Increments with each open, so an editor can tell one workspace from the next. */
  readonly id: number;
  /** The official document the workspace opened on (its manifest name) and that document's manifest role. */
  readonly documentName: string;
  readonly role: DocumentRole;
  /** The draft the workspace was loaded from, or `null` for the official build itself. */
  readonly draftId: string | null;
  /** The document's own source file. */
  readonly entry: string;
  /** The source the island check and the preview compose: the manifest's composed island root for a fragment, the
   * document's own source otherwise, or `null` when there is nothing the studio can compose (`rootRefusal` says why). */
  readonly root: string | null;
  readonly rootRefusal: string | null;
  readonly files: Readonly<Record<string, WorkspaceFile>>;
  /** The file the editor shows. */
  readonly active: string;
  /** Increments with each source change, in any file. */
  readonly revision: number;
  readonly diagnostics: Readonly<Record<string, FileDiagnostics>>;
  /** The latest island check the machine accepted, or `null` before the first. */
  readonly island: IslandCheck | null;
  /** The newest clean composition's document, which the Spatial and State views show; it outlives a refused check
   * so an error in progress does not empty them. */
  readonly composition: unknown;
  readonly reveal: SourceReveal | null;
}

/** The compiled IR of one source for the Compiled and Sections views: the island check's compile at its revision, or
 * one fetched on request. */
export interface CompiledView {
  readonly workspaceId: number;
  readonly path: string;
  readonly revision: number;
  readonly result: SourceCompileResult;
  /** `result.document` parsed with 64-bit literals kept exact, or `null` when compilation refused. */
  readonly value: unknown;
  readonly sourceMap: SourceMap;
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
  /** The composed document this preview compiled, which every history move recompiles. */
  readonly composed: string | null;
}

export interface SelectionState {
  readonly topology: string | null;
  readonly ordinals: readonly number[];
  readonly hovered: number | null;
}

export interface StudioContext {
  readonly official: OfficialLoad | null;
  /** Hosts the language server; boots with the studio. */
  readonly languageEngine: WorldEngine | null;
  /** The language engine's language server channel, shared by the editor's client and the diagnostics stream. */
  readonly languageChannel: LanguageServerChannel | null;
  /** Hosts composition, preview, the console, and geometry; boots on first need. */
  readonly worldEngine: WorldEngine | null;
  readonly boot: BootState;
  readonly language: LanguageEngineState;
  readonly world: WorldEngineState;
  readonly workspace: WorkspaceState | null;
  readonly compiled: CompiledView | null;
  readonly geometry: Readonly<Record<string, readonly EngineCell[]>>;
  readonly selection: SelectionState;
  readonly preview: PreviewState;
  /** The local draft library, read when the studio starts and again after each save or delete. */
  readonly drafts: DraftListing;
  /** What the studio itself refused (an open, a save, a compile request), newest last; cleared by the next open. */
  readonly refusals: readonly string[];
  /** The machine's own construction input, carried into context because an invoked actor's `input` factory only
   * sees `{context, event}`. `draftStore` is resolved to a concrete store at construction. */
  readonly machineInput: StudioMachineInput & { readonly draftStore: LocalDraftStore };
}

export type StudioEvent =
  | { type: "OPEN_OFFICIAL"; name: string }
  | { type: "LOAD_DRAFT"; id: string }
  | { type: "OPEN_FILE"; path: string }
  | { type: "SOURCE_CHANGED"; path: string; version: number; text: string }
  | { type: "DIAGNOSTICS"; path: string; version: number | null; diagnostics: readonly SourceDiagnostic[] }
  | { type: "ISLAND_CHECKED"; check: Omit<IslandCheck, "value"> }
  | { type: "LANGUAGE_FAILED"; message: string }
  | { type: "WORLD_FAILED"; message: string }
  | { type: "REVEAL_SOURCE"; path: string; line: number; column: number; length: number }
  | { type: "REQUEST_COMPILED" }
  | { type: "SAVE_DRAFT"; id?: string; title?: string }
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

/** Opens that read the workspace from somewhere first (the official build, or a draft over it). */
export const OPEN_EVENTS = ["OPEN_OFFICIAL", "LOAD_DRAFT"] as const;

/** Events that change what the preview would compile, so a running preview stops. */
export const SOURCE_EVENTS = ["SOURCE_CHANGED", ...OPEN_EVENTS] as const;
