// The TypeScript facade's own contract for Puck.World.Browser's [JSExport] surface — see
// src/Puck.World.Browser/README.md's API table for the raw wire shape this facade decodes. Every 64-bit value
// crosses the wasm boundary as a decimal string (Puck.World.Browser.Engine.LongAsStringJsonConverter /
// UInt64AsStringJsonConverter) and surfaces here as a `bigint`, never a JavaScript `number`.

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
 * surface. `refusals` condenses each structured `BrowserRefusal` into one readable line. */
export interface JudgeTrace {
  rules: { name: string; mode: string; evaluations: unknown[] }[];
  writes: { row: string; key: string | null; old: bigint; new: bigint }[];
  refusals: string[];
  /** Every world-scoped operand this tick read from the hostless reader (no bodies, machines, clocks, or
   * adjacencies exist in the browser build), with the quiescent answer it was given. */
  hostFacts: { rule: string; operand: string; answer: string }[];
}

/** The engine handle every host implementation (inline or worker) exposes identically. */
export interface WorldEngine {
  version(): Promise<{ schemaVersion: string; engine: string; commit: string }>;
  parse(json: string): Promise<ParseResult>;
  parseFragment(fragmentJson: string, hostJson: string, alias: string): Promise<ParseResult>;
  /** Composes a whole basis-and-imports graph purely from `documents` — every document of an import tree keyed by
   * its worlds-relative name (`"puck.world.json"`, `"standard.basis.json"`, `"games/tictactoe.world.json"`), the
   * engine resolving `imports[].document` against those keys exactly as `WorldDefinitionFileSource` resolves them
   * on disk. When `edited` is given its text replaces that keyed document, and every diagnostic is mapped back to
   * the edited document's own paths (alias prefix stripped) — a diagnostic belonging to a different document is
   * prefixed `"<name>: "` instead. `composed` carries the composed standalone document's JSON on success. */
  composeTree(
    rootName: string,
    documents: Record<string, string>,
    edited?: { name: string; json: string },
  ): Promise<ParseResult & { composed?: string }>;
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
  dispose(): Promise<void>;
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
