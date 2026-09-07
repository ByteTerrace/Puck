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

/** One state row's whole current content, authored cells only (`BrowserRowSnapshot`). */
export interface RowInfo {
  name: string;
  kind: "Int" | "Fixed" | "Bool" | "Text";
  keyed: boolean;
  cells: { key: string; value: bigint; text: string }[];
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
  release(handle: string): Promise<void>;
  rows(handle: string): Promise<RowInfo[]>;
  rebind(handle: string, json: string): Promise<{ ok: boolean; error?: string }>;
  judge(handle: string, tick: bigint): Promise<{ ok: true; trace: JudgeTrace } | { ok: false; error: string }>;
  readRow(handle: string, row: string, key?: string): Promise<{ found: boolean; value: bigint; text: string }>;
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
