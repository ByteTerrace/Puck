// The `'inline'` engine host: boots Puck.World.Browser's own main.mjs directly in the calling thread (the main
// thread in a browser tab, or the Node process under `node --test`) and wraps its synchronous, JSON-string
// `[JSExport]` surface into the async, bigint-typed `WorldEngine` facade. `wrapRawExports` carries no import of its
// own beyond `engineTypes` — `engine.worker.ts` reuses it unmodified inside a Worker's own global scope, so the
// wire-decoding rules (which field is a decimal-string 64-bit value, which failure shape throws versus returns an
// `ok:false` arm) exist in exactly one place.
import type { CellKindName, CellValue, EngineCell, EngineCostBound, EngineCostReport, EngineDiagnostic, EngineHostOptions, JudgeTrace, ParseResult, RowInfo, WorldEngine } from "./engineTypes";

/** The raw `[JSExport]` surface `main.mjs`'s `createEngine()` resolves — every member synchronous, taking and
 * returning JSON strings (see `Puck.World.Browser.Exports.BrowserExports`). */
export interface RawBrowserExports {
  Version(): string;
  Parse(json: string): string;
  ParseFragment(fragmentJson: string, hostJson: string, alias: string): string;
  ComposeTree(rootName: string, documentsJson: string, editedName: string, editedJson: string): string;
  Canonicalize(json: string): string;
  Compile(json: string): string;
  AnalyzeCosts(json: string): string;
  Release(handle: string): string;
  Rows(handle: string): string;
  Costs(handle: string): string;
  Rebind(handle: string, json: string): string;
  Judge(handle: string, tick: string): string;
  ReadRow(handle: string, row: string, key: string): string;
  WriteRow(handle: string, row: string, key: string, value: string, write: string): string;
  Evaluate(handle: string, expression: string, kind: string, tick: string): string;
  BoardMask(handle: string, row: string): string;
  StateHash(handle: string): string;
  Cells(topologyJson: string): string;
}

/** `main.mjs`'s own `createEngine(options)` signature — the subset `wrapRawExports`'s callers need. */
export type CreateRawEngine = (options?: {
  resourceLoader?: EngineHostOptions["resourceLoader"];
}) => Promise<RawBrowserExports>;

// The scalar-row key `Puck.State.StateRow.SlotKey` addresses — an omitted `key` argument means "the scalar slot",
// never the empty string (`CellName.TryParse` refuses an empty candidate outright).
const ScalarSlotKey = "$value";

/** Decodes the wire's tag-and-payload pair into the one carrier — see `Puck.World.Browser/README.md`'s
 * "One cell value on the wire" for each kind's spelling. `null` where the arena holds no such cell. */
function decodeCellValue(kind: string | null, value: string | null): CellValue | null {
  if (kind === null || value === null) return null;

  const tag = kind as CellKindName;

  switch (tag) {
    case "Int":
    case "Fixed":
      return { kind: tag, value: BigInt(value) };
    case "Bool":
      return { kind: "Bool", value: value === "true" };
    case "Text":
      return { kind: "Text", value };
    case "Vector":
      return { kind: "Vector", value };
    default:
      throw new Error(`'${kind}' is not a cell kind this facade decodes.`);
  }
}
function mapDiagnostics(errors: readonly { path: string | null; message: string }[]): EngineDiagnostic[] {
  return errors.map((error) => ({ path: error.path ?? "", message: error.message }));
}
type WireCostBound = { kind: string; cycles: string | null; reason: string | null };
function decodeCostBound(bound: WireCostBound): EngineCostBound {
  if (bound.kind === "Known" && bound.cycles !== null) {
    return { kind: "Known", cycles: BigInt(bound.cycles), reason: null };
  }
  if ((bound.kind === "Unmodeled" || bound.kind === "Overflow") && bound.cycles === null) {
    return { kind: bound.kind, cycles: null, reason: bound.reason };
  }
  throw new Error("The engine returned an invalid cost bound.");
}
type WireCostReport = Omit<EngineCostReport, "stepAllowanceCycles" | "recurringBound" | "searchReservations" | "totalBound" | "editBurstBound" | "contributors" | "resources"> & {
  stepAllowanceCycles: string;
  recurringBound: WireCostBound;
  searchReservations: WireCostBound;
  totalBound: WireCostBound;
  editBurstBound: WireCostBound;
  contributors: (Omit<EngineCostReport["contributors"][number], "multiplier"> & { multiplier: string })[];
  resources: Omit<EngineCostReport["resources"], "vectorComponentBytes" | "layoutBytes" | "retainedVisibilityBytes" | "retainedKeyBytes" | "arenaFootprintBytes" | "arenaAdmissionCeilingBytes" | "journalAllowanceBytes"> & {
    vectorComponentBytes: string; layoutBytes: string | null; retainedVisibilityBytes: string | null;
    retainedKeyBytes: string | null; arenaFootprintBytes: string | null; arenaAdmissionCeilingBytes: string;
    journalAllowanceBytes: string;
  };
};
function mapCostReport(report: WireCostReport): EngineCostReport {
  return {
    ...report,
    stepAllowanceCycles: BigInt(report.stepAllowanceCycles),
    recurringBound: decodeCostBound(report.recurringBound),
    searchReservations: decodeCostBound(report.searchReservations),
    totalBound: decodeCostBound(report.totalBound),
    editBurstBound: decodeCostBound(report.editBurstBound),
    contributors: report.contributors.map(line => ({ ...line, multiplier: BigInt(line.multiplier) })),
    resources: {
      ...report.resources,
      vectorComponentBytes: BigInt(report.resources.vectorComponentBytes),
      layoutBytes: report.resources.layoutBytes === null ? null : BigInt(report.resources.layoutBytes),
      retainedVisibilityBytes: report.resources.retainedVisibilityBytes === null ? null : BigInt(report.resources.retainedVisibilityBytes),
      retainedKeyBytes: report.resources.retainedKeyBytes === null ? null : BigInt(report.resources.retainedKeyBytes),
      arenaFootprintBytes: report.resources.arenaFootprintBytes === null ? null : BigInt(report.resources.arenaFootprintBytes),
      arenaAdmissionCeilingBytes: BigInt(report.resources.arenaAdmissionCeilingBytes),
      journalAllowanceBytes: BigInt(report.resources.journalAllowanceBytes),
    },
  };
}
function mapParseResult(rawJson: string): ParseResult {
  const raw = JSON.parse(rawJson) as
    | { ok: true; document: string; deferred: string[] | null }
    | { ok: false; errors: { path: string | null; message: string }[]; deferred: string[] | null };

  return (raw.ok
    ? { ok: true, document: JSON.parse(raw.document), deferred: raw.deferred ?? [] }
    : { ok: false, errors: mapDiagnostics(raw.errors), deferred: raw.deferred ?? [] }
  );
}
function formatRefusal(refusal: { category: string; count: string; lastTick: string; rule: string; effect: string; detail: string }): string {
  return `${refusal.category} in rule '${refusal.rule}' effect '${refusal.effect}' (x${refusal.count}, last tick ${refusal.lastTick}): ${refusal.detail}`;
}
function mapJudgeTrace(raw: {
  rules: { name: string; mode: string; evaluations: unknown[] }[];
  writes: { row: string; key: string; old: string; new: string }[];
  refusals: { category: string; count: string; lastTick: string; rule: string; effect: string; detail: string }[];
  hostFacts?: { rule: string; operand: string; answer: string }[];
}): JudgeTrace {
  return {
    rules: raw.rules,
    writes: raw.writes.map((write) => ({ row: write.row, key: write.key, old: BigInt(write.old), new: BigInt(write.new) })),
    refusals: raw.refusals.map(formatRefusal),
    hostFacts: raw.hostFacts ?? [],
  };
}

/** Wraps a booted engine's raw `[JSExport]` surface into the `WorldEngine` facade — the one place every export's
 * wire shape is decoded, shared by `inline` and `worker` hosting. `disposeCore` lets a caller (the worker script's
 * own message loop) supply a real teardown; the inline caller passes none, since an in-process Mono runtime has no
 * supported unload path of its own. */
export function wrapRawExports(raw: RawBrowserExports, disposeCore?: () => void): WorldEngine {
  return {
    async costs(handle) {
      const result = JSON.parse(raw.Costs(handle)) as { ok: true; report: WireCostReport } | { ok: false; error: string };
      if (!result.ok) throw new Error(result.error);
      return mapCostReport(result.report);
    },
    async analyzeCosts(json) {
      const result = JSON.parse(raw.AnalyzeCosts(json)) as
        | { ok: true; validated: boolean; report: WireCostReport; validationErrors: { path: string | null; message: string }[] | null; deferred: string[] | null }
        | { ok: false; validated: false; errors: { path: string | null; message: string }[] };
      return result.ok
        ? { ok: true, validated: result.validated, report: mapCostReport(result.report), validationErrors: mapDiagnostics(result.validationErrors ?? []), deferred: result.deferred ?? [] }
        : { ok: false, validated: false, errors: mapDiagnostics(result.errors) };
    },
    async version() {
      return JSON.parse(raw.Version()) as { schemaVersion: string; engine: string; commit: string };
    },
    async parse(json) {
      return mapParseResult(raw.Parse(json));
    },
    async parseFragment(fragmentJson, hostJson, alias) {
      return mapParseResult(raw.ParseFragment(fragmentJson, hostJson, alias));
    },
    async composeTree(rootName, documents, edited) {
      const raw2 = JSON.parse(
        raw.ComposeTree(rootName, JSON.stringify(documents), edited?.name ?? "", edited?.json ?? ""),
      ) as
        | { ok: true; composed: string; document: string; deferred: string[] | null }
        | { ok: false; errors: { path: string | null; message: string }[]; deferred: string[] | null };

      return (raw2.ok
        ? { ok: true, composed: raw2.composed, document: JSON.parse(raw2.document), deferred: raw2.deferred ?? [] }
        : { ok: false, errors: mapDiagnostics(raw2.errors), deferred: raw2.deferred ?? [] }
      );
    },
    async canonicalize(json) {
      return mapParseResult(raw.Canonicalize(json));
    },
    async compile(json) {
      const result = JSON.parse(raw.Compile(json)) as { ok: true; handle: string } | { ok: false; errors: { path: string | null; message: string }[] };

      return (result.ok ? result : { ok: false, errors: mapDiagnostics(result.errors) });
    },
    async release(handle) {
      raw.Release(handle);
    },
    async rows(handle) {
      const result = JSON.parse(raw.Rows(handle)) as { ok: boolean; rows?: { name: string; kind: string; keyed: boolean; cells: { key: string; value: string | null }[] }[]; error?: string };

      if (!result.ok) throw new Error(result.error ?? `rows: handle '${handle}' names no live session.`);

      // A snapshot carries the tag once for the whole row, so each cell spells its payload alone.
      return (result.rows ?? []).map((row): RowInfo => ({
        name: row.name,
        kind: row.kind as CellKindName,
        keyed: row.keyed,
        cells: row.cells.map((cell) => ({ key: cell.key, value: decodeCellValue(row.kind, cell.value) })),
      }));
    },
    async rebind(handle, json) {
      return JSON.parse(raw.Rebind(handle, json)) as { ok: boolean; error?: string };
    },
    async judge(handle, tick) {
      const result = JSON.parse(raw.Judge(handle, tick.toString())) as { ok: true; trace: Parameters<typeof mapJudgeTrace>[0] } | { ok: false; error: string };

      return (result.ok ? { ok: true, trace: mapJudgeTrace(result.trace) } : result);
    },
    async readRow(handle, row, key) {
      const result = JSON.parse(raw.ReadRow(handle, row, key ?? ScalarSlotKey)) as { found: boolean; kind: string | null; value: string | null };

      return { found: result.found, value: decodeCellValue(result.kind, result.value) };
    },
    async writeRow(handle, row, key, value, write) {
      return JSON.parse(raw.WriteRow(handle, row, key ?? ScalarSlotKey, value.toString(), write)) as { ok: boolean; error?: string };
    },
    async evaluate(handle, expression, kind, tick) {
      const result = JSON.parse(raw.Evaluate(handle, expression, kind, tick.toString())) as { ok: boolean; value: string; error?: string };

      return (result.ok ? { ok: true, value: BigInt(result.value) } : { ok: false, error: result.error });
    },
    async boardMask(handle, row) {
      const result = JSON.parse(raw.BoardMask(handle, row)) as { ok: boolean; mask: string; error?: string };

      return (result.ok ? { ok: true, mask: BigInt(result.mask) } : { ok: false, error: result.error });
    },
    async stateHash(handle) {
      const result = JSON.parse(raw.StateHash(handle)) as { ok: boolean; hash: string; error?: string };

      if (!result.ok) throw new Error(result.error ?? `stateHash: handle '${handle}' names no live session.`);

      return result.hash;
    },
    async cells(topologyJson) {
      return JSON.parse(raw.Cells(topologyJson)) as { ok: true; cells: EngineCell[] } | { ok: false; error: string };
    },
    async dispose() {
      disposeCore?.();
    },
  };
}

/**
 * A real, un-rewritten dynamic `import()` — usable with `file:`/`data:` URL specifiers, not just
 * bare package names. TypeScript's CommonJS emit rewrites every `import(x)` into a
 * `require()`-based emulation regardless of whether `x` is a literal, and `require()` has no
 * notion of a `file:`/`data:` URL the way ECMAScript's own dynamic import operator does — this
 * indirection (`new Function` hides the call from both TypeScript's and Vite/Rollup's static
 * import analysis) keeps it a genuine dynamic import at runtime under every module system this
 * file is ever loaded by: bundled ESM in the studio, or CommonJS-transpiled for this repository's
 * `require.extensions['.ts']` Node test harness (see tests/engineBoot.test.cjs's own remarks).
 * Exported so every other file needing the same guarantee (workerBoot.ts, engineBoot.ts,
 * engine.worker.ts) shares this one indirection rather than re-deriving it.
 */
export const dynamicImport: (specifier: string) => Promise<unknown> = new Function(
  "specifier",
  "return import(specifier);",
) as (specifier: string) => Promise<unknown>;

/** Boots `options.engineEntryUrl` (a `main.mjs`) in the calling thread and returns the wrapped `WorldEngine`. */
export async function createInlineWorldEngine(options: EngineHostOptions): Promise<WorldEngine> {
  const module = (await dynamicImport(options.engineEntryUrl)) as { createEngine: CreateRawEngine };
  const raw = await module.createEngine(options.resourceLoader ? { resourceLoader: options.resourceLoader } : undefined);

  return wrapRawExports(raw);
}
