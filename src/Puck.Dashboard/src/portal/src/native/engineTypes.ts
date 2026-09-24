// The TypeScript facade's own contract for Puck.World.Browser's [JSExport] surface — see
// src/Puck.World.Browser/README.md's API table for the raw wire shape this facade decodes. Every 64-bit value
// crosses the wasm boundary as a decimal string (Puck.World.Browser.Engine.LongAsStringJsonConverter /
// UInt64AsStringJsonConverter) and surfaces here as a `bigint`, never a JavaScript `number`.
import type { Observable } from "rxjs";
import type { WasmCallCounts } from "./wasmCounts";

/** One validator diagnostic: a leading path token (when the message spells one) and the remaining prose — see
 * `BrowserErrorPaths.Split`. */
export interface EngineDiagnostic {
  path: string;
  message: string;
}

/** Reference cycles are exact counts only when evidence establishes a bound. */
export type EngineCostBound =
  | { kind: "Known"; cycles: bigint; reason: null }
  | { kind: "Unmodeled" | "Overflow"; cycles: null; reason: string | null };

/** Authored analysis shared with the server. Heuristic work strings may themselves state an unresolved bound. */
export interface EngineCostReport {
  modelId: string;
  evidenceDigest: string | null;
  scope: string;
  simulationRateHz: number;
  stepAllowanceCycles: bigint;
  recurringBound: EngineCostBound;
  searchReservations: EngineCostBound;
  totalBound: EngineCostBound;
  editBurstBound: EngineCostBound;
  admitted: boolean;
  heuristicWorkUnitsPerTick: string;
  contributors: { name: string; isInteraction: boolean; multiplier: bigint; setup: string; check: string; fire: string; work: string }[];
  contributorSources: { name: string; isInteraction: boolean; jsonPointer: string; sourcePath: string | null; line: number | null; column: number | null; moduleInstancePath: string | null }[];
  issues: string[];
  resources: {
    rowCount: number; topologyCount: number; populationCapacity: number; cellSlotCount: number;
    vectorComponentBytes: bigint; laneSlotCount: number; laneRosterCount: number; drawMaskWordCount: number;
    layoutBytes: bigint | null; retainedVisibilityBytes: bigint | null; retainedKeyBytes: bigint | null;
    arenaFootprintBytes: bigint | null; arenaAdmissionCeilingBytes: bigint; journalAllowanceBytes: bigint;
    measurementIssue: string | null; unmodeledTotalMemoryReason: string;
  };
}

/** The shared `Parse`/`ParseFragment`/`Canonicalize` outcome shape. */
export type ParseResult =
  | { ok: true; document: unknown; deferred: string[] }
  | { ok: false; errors: EngineDiagnostic[]; deferred: string[] };

/** One compiled topology cell's ordinal world-space geometry (`BrowserCellGeometry`). */
export interface EngineCell {
  ordinal: number;
  key: string;
  x: number;
  y: number;
  z: number;
}

/** The closed set of cell kinds a state row declares (`Puck.State.CellKind`). */
export type CellKindName = "Int" | "Fixed" | "Bool" | "Text" | "Vector";

/** One cell's value as the single carrier its kind declares (`Puck.State.CellValue`), decoded from the wire's
 * tag-and-payload pair. `Int` and `Fixed` carry raw 64-bit bits — a `Fixed` carrier is the raw `FixedQ4816`
 * pattern, the same channel `writeRow` takes, never a decimal reading of it. A `Vector` carries its base64url
 * components. */
export type CellValue =
  | { kind: "Int" | "Fixed"; value: bigint }
  | { kind: "Bool"; value: boolean }
  | { kind: "Text"; value: string }
  | { kind: "Vector"; value: string };

/** Returns the raw 64-bit word a numeric carrier holds, or `null` for an absent cell or a carrier holding text, a
 * bool, or a vector — the read a numeric surface (a register input, a board colouring) needs. */
export function cellNumber(value: CellValue | null | undefined): bigint | null {
  return ((value && ((value.kind === "Int") || (value.kind === "Fixed"))) ? value.value : null);
}

/** Returns one carrier as the text a read-back surface displays, or `null` for an absent cell. */
export function cellText(value: CellValue | null | undefined): string | null {
  return (value ? String(value.value) : null);
}

/** Returns whether two carriers hold the same case with the same payload. */
export function sameCellValue(left: CellValue | null | undefined, right: CellValue | null | undefined): boolean {
  if (!left || !right) return (!left && !right);
  return (left.kind === right.kind && left.value === right.value);
}

/** One state row's whole current content, authored cells only (`BrowserRowSnapshot`). A cell's `value` is `null`
 * where the arena holds no such cell. */
export interface RowInfo {
  name: string;
  kind: CellKindName;
  keyed: boolean;
  cells: { key: string; value: CellValue | null }[];
}

/** One `Judge` call's whole trace (`BrowserJudgeResult`). Each rule's own captured evaluations stay opaque
 * (`unknown`) — they carry no bigint-conversion contract of their own; `writes` and `refusals` are the typed
 * surface. `refusals` condenses each structured `BrowserRefusal` into one readable line, beside the rule it names. */
export interface JudgeTrace {
  rules: { name: string; mode: string; evaluations: unknown[] }[];
  writes: { row: string; key: string | null; old: bigint; new: bigint }[];
  refusals: { rule: string; text: string }[];
  /** Every world-scoped operand this tick read from the hostless reader (no bodies, machines, clocks, or
   * adjacencies exist in the browser build), with the quiescent answer it was given. */
  hostFacts: { rule: string; operand: string; answer: string }[];
}

/** How serious a source diagnostic is, in the language server's own three levels. */
export type SourceSeverity = "error" | "warning" | "information";

/** One `.puck` compiler diagnostic, located in its source file: `path` is worlds-relative (`games/klondike.puck`),
 * `line` and `column` are 1-based, and `length` counts characters from there. Line 0 means the finding is about the
 * whole document rather than one place in it. */
export interface SourceDiagnostic {
  code: string;
  severity: SourceSeverity;
  message: string;
  path: string;
  line: number;
  column: number;
  length: number;
}

/** Where a compiled value came from: a source span, plus the module instance that produced it when a `use` or a
 * `world name = module(...)` expanded it (`null` for text written at the document's own root). */
export interface SourceSpan {
  path: string;
  line: number;
  column: number;
  length: number;
  module: string | null;
}

/** Every compiled value's source span, keyed by its RFC 6901 JSON pointer into the compiled document. A pointer
 * missing from the map resolves through its nearest mapped ancestor (see `authoring/sourceMap.ts`). */
export type SourceMap = Readonly<Record<string, SourceSpan>>;

/** One world a composition source declares, as JSON text (parse it with `document/jsonText.ts` so 64-bit literals
 * stay exact), with its own source map; `entry` marks the world the source boots. */
export interface CompiledWorld {
  name: string;
  document: string;
  entry: boolean;
  sourceMap: SourceMap;
}

/** `compileSource`'s answer: the compiled document (or, for a source that declares several worlds, `null` and one
 * entry per world), every diagnostic the language server would publish at both tiers, and the map from each
 * compiled value back to its source. `document` is also `null` when compilation refused. */
export interface SourceCompileResult {
  ok: boolean;
  document: string | null;
  worlds: CompiledWorld[];
  diagnostics: SourceDiagnostic[];
  sourceMap: SourceMap;
}

/** `composeSource`'s answer: the root composed through its whole basis-and-imports graph as one standalone document
 * (a composition source composes its entry world; a `.world.json` root composes as JSON), and the composed world's
 * validation. `ok` means it composed and no diagnostic is an error; `composed` is present whenever composition itself
 * succeeded, valid or not. A check the engine defers (no machine catalog in the browser) is an `information`
 * diagnostic. A composed-world finding names the root at line 0 (the whole document), even when an imported file
 * caused it. */
export interface SourceComposeResult {
  ok: boolean;
  composed: string | null;
  diagnostics: SourceDiagnostic[];
}

/** One JSON-RPC message as the language server wrote it: a response, a request, or a notification. */
export type LspMessage = { jsonrpc: "2.0"; id?: number | string | null; method?: string; params?: unknown; result?: unknown; error?: unknown };

/** One `lspIdle` call's answer: whether a unit of diagnostic work ran, whether more is pending, and what the unit
 * wrote. */
export interface LspIdleResult {
  ran: boolean;
  pending: boolean;
  messages: LspMessage[];
}

/**
 * The studio's connection to an engine's language server. `send` takes one client JSON-RPC message; `messages()`
 * answers the one shared stream of every message the server writes, in order, for as long as the channel is open.
 * The engine runs its own pump behind the channel (`native/languagePump.ts`): client messages first, then pending
 * diagnostic work one unit at a time while nothing else is waiting. The channel is all methods, so the studio
 * machine's context that holds it stays persistable.
 */
export interface LanguageServerChannel {
  send(message: string): void;
  messages(): Observable<LspMessage>;
  close(): void;
}

/** The engine handle every host implementation (inline or worker) exposes identically. */
export interface WorldEngine {
  version(): Promise<{ schemaVersion: string; engine: string; commit: string }>;
  parse(json: string): Promise<ParseResult>;
  parseFragment(fragmentJson: string, hostJson: string, alias: string): Promise<ParseResult>;
  canonicalize(json: string): Promise<ParseResult>;
  compile(json: string): Promise<{ ok: true; handle: string } | { ok: false; errors: EngineDiagnostic[] }>;
  /** Analyzes a structurally compilable draft without installing a session or allocating an arena. */
  analyzeCosts(json: string): Promise<
    | { ok: true; validated: boolean; report: EngineCostReport; validationErrors: EngineDiagnostic[]; deferred: string[] }
    | { ok: false; validated: false; errors: EngineDiagnostic[] }
  >;
  release(handle: string): Promise<void>;
  rows(handle: string): Promise<RowInfo[]>;
  /** Reads cached analysis for the installed document. Rebinding invalidates that compilation's report. */
  costs(handle: string): Promise<EngineCostReport>;
  rebind(handle: string, json: string): Promise<{ ok: boolean; error?: string }>;
  judge(handle: string, tick: bigint): Promise<{ ok: true; trace: JudgeTrace } | { ok: false; error: string }>;
  readRow(handle: string, row: string, key?: string): Promise<{ found: boolean; value: CellValue | null }>;
  writeRow(handle: string, row: string, key: string | undefined, value: bigint, write: "set" | "add"): Promise<{ ok: boolean; error?: string }>;
  evaluate(handle: string, expression: string, kind: "Int" | "Fixed", tick: bigint): Promise<{ ok: boolean; value?: bigint; error?: string }>;
  boardMask(handle: string, row: string): Promise<{ ok: boolean; mask?: bigint; error?: string }>;
  stateHash(handle: string): Promise<string>;
  cells(topologyJson: string): Promise<{ ok: true; cells: EngineCell[] } | { ok: false; error: string }>;
  /** Replaces the engine's source workspace with `files`, keyed by worlds-relative path (`games/klondike.puck`).
   * Every later source call resolves imports and `basis` references against this set. */
  mountSources(files: Readonly<Record<string, string>>): Promise<void>;
  /** Writes one workspace file's text, adding the file when the workspace has none by that path. */
  writeSource(path: string, text: string): Promise<void>;
  /** Compiles one workspace source, with every diagnostic at both tiers, its IR, and its source map. */
  compileSource(path: string): Promise<SourceCompileResult>;
  /** Composes one workspace root through its basis and imports into one standalone document. */
  composeSource(path: string): Promise<SourceComposeResult>;
  /** Hands the language server one client message and returns what it writes back at once. The server never
   * diagnoses here: diagnostics wait for `lspIdle`. A `didOpen` or `didChange` for a `file:///worlds/…` URI writes
   * its text through into the mounted workspace. */
  lsp(message: string): Promise<LspMessage[]>;
  /** Runs one unit of the server's pending diagnostic work; call it again while the answer says more is pending. */
  lspIdle(): Promise<LspIdleResult>;
  /** Opens the language server channel. An engine has one language server, so it has at most one open channel. */
  languageServer(): LanguageServerChannel;
  /** The engine's compiled `dotnet.native.wasm` when it booted from an official build: another engine in the same
   * session boots from it and only instantiates, never compiling the module again. */
  readonly wasmModule?: WebAssembly.Module;
  /** How many times this engine's JS realm compiled or instantiated WebAssembly (`native/wasmCounts.ts`). */
  wasmCounts(): Promise<WasmCallCounts>;
  dispose(): Promise<void>;
}

/** An engine's decoded calls, before a host gives it its language server (`native/languagePump.ts`'s
 * `withLanguageServer`). The worker holds one of these; the page's thread runs the pump. */
export type EngineCore = Omit<WorldEngine, "languageServer">;

/** Thrown by a source or language call when the booted engine build has no such export. */
export class EngineCapabilityMissing extends Error {
  readonly exportName: string;

  constructor(exportName: string) {
    super(`this engine build has no '${exportName}' export.`);
    this.name = "EngineCapabilityMissing";
    this.exportName = exportName;
  }
}

/** `dotnet.d.ts`'s `LoadBootResourceCallback` shape (see `Puck.World.Browser/main.mjs`'s own remarks) — a caller
 * feeds content-hash-verified cached bytes instead of a network fetch. Meaningful only in `'inline'` mode: a
 * function reference cannot cross a Web Worker boundary, so `'worker'` mode never forwards it (see
 * `engine.worker.ts`'s own remarks). */
export type EngineResourceLoader = (
  type: string,
  name: string,
  defaultUri: string,
  integrity: string,
  behavior: string,
) => string | Promise<Response> | null | undefined;

/** Options `createEngineHost` accepts. */
export interface EngineHostOptions {
  mode: "inline" | "worker";
  /** The URL of `Puck.World.Browser`'s own `main.mjs` — the one entry point both hosting modes import. */
  engineEntryUrl: string;
  resourceLoader?: EngineResourceLoader;
}
