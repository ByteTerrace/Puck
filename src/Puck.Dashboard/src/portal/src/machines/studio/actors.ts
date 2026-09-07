/**
 * Every engine/store call the studio machine makes, each wrapped in `fromPromise` so a
 * synchronous throw (an `IntakeRefusal`, a malformed paint target) becomes a catchable `onError`
 * transition instead of an exception escaping the interpreter. Split out of `studioMachine.ts`
 * purely for length — these are plain typed values with no dependency on that file's own
 * `setup(...)` builder, referenced there only by the string names `studioMachine.ts` registers
 * them under.
 */
import { fromPromise } from "xstate";
import { validateDocument, type ValidationOutcome } from "./document";
import { computeGeometry, type GeometryOutcome } from "./geometry";
import { compilePreview, replayPreview, tickPreview, writePreviewRow, type PreviewOutcome } from "./preview";
import type { DocumentState, PreviewScriptStep, PreviewSnapshot, StudioMachineInput } from "./types";
import type { StudioDraft } from "../../document/localDrafts";
import { loadOfficial } from "../../official/officialClient";
import type { RowInfo, WorldEngine } from "../../native/engineTypes";

export interface EditInput {
  readonly perform: () => DocumentState | Promise<DocumentState>;
}
export const editActor = fromPromise<DocumentState, EditInput>(async ({ input }) => input.perform());

export interface BootOutput {
  readonly officialLoad: Awaited<ReturnType<typeof loadOfficial>>;
  readonly engine: WorldEngine;
  readonly version: { readonly schemaVersion: string; readonly engine: string; readonly commit: string };
}
export const bootActor = fromPromise<BootOutput, StudioMachineInput>(async ({ input }) => {
  const officialLoad = await loadOfficial(input.official, input.fetchImpl, input.byteStore);
  const engine = await input.bootEngine(officialLoad, { mode: input.engineMode, fetchImpl: input.fetchImpl });
  const version = await engine.version();
  return { officialLoad, engine, version };
});

export interface ValidateInput {
  readonly engine: WorldEngine;
  readonly official: BootOutput["officialLoad"];
  readonly document: Pick<DocumentState, "name" | "role" | "text" | "value">;
}
export const validateActor = fromPromise<ValidationOutcome, ValidateInput>(async ({ input }) =>
  validateDocument(input.engine, input.official, input.document));

export interface GeometryInput {
  readonly engine: WorldEngine;
  readonly value: unknown;
}
export const geometryActor = fromPromise<GeometryOutcome, GeometryInput>(async ({ input }) =>
  computeGeometry(input.engine, input.value));

export interface CompileInput {
  readonly engine: WorldEngine;
  readonly sourceJson: string;
}
export type CompileOutput = PreviewOutcome & { readonly snapshot: PreviewSnapshot | null };
export const compileActor = fromPromise<CompileOutput, CompileInput>(async ({ input }) => compilePreview(input.engine, input.sourceJson));

export interface WriteInput {
  readonly engine: WorldEngine;
  readonly handle: string;
  readonly row: string;
  readonly key?: string;
  readonly value: bigint;
  readonly write: "set" | "add";
}
export type WriteOutput =
  | { readonly ok: true; readonly rows: readonly RowInfo[]; readonly step: PreviewScriptStep }
  | { readonly ok: false; readonly error: string };
export const writeActor = fromPromise<WriteOutput, WriteInput>(async ({ input }) => {
  const result = await writePreviewRow(input.engine, input.handle, input.row, input.key, input.value, input.write);
  if (!result.ok) {
    return result;
  }
  return { ok: true, rows: result.rows, step: { kind: "write", row: input.row, key: input.key, value: input.value, write: input.write } };
});

export interface TickInput {
  readonly engine: WorldEngine;
  readonly handle: string;
  readonly nextTick: bigint;
  readonly scriptLength: number;
}
export type TickOutput = { readonly ok: true; readonly snapshot: PreviewSnapshot } | { readonly ok: false; readonly error: string };
export const tickActor = fromPromise<TickOutput, TickInput>(async ({ input }) =>
  tickPreview(input.engine, input.handle, input.nextTick, input.scriptLength));

export interface ReplayInput {
  readonly engine: WorldEngine;
  readonly sourceJson: string;
  readonly script: readonly PreviewScriptStep[];
  readonly upTo: number;
  readonly index: number;
}
export type ReplayOutput = PreviewOutcome & { readonly index: number };
export const replayActor = fromPromise<ReplayOutput, ReplayInput>(async ({ input }) => {
  const outcome = await replayPreview(input.engine, input.sourceJson, input.script, input.upTo);
  return { ...outcome, index: input.index };
});

export interface SaveDraftInput {
  readonly perform: () => StudioDraft;
  readonly revision: number;
}
export type SaveDraftOutput = { readonly draft: StudioDraft; readonly revision: number };
export const saveDraftActor = fromPromise<SaveDraftOutput, SaveDraftInput>(async ({ input }) => ({ draft: input.perform(), revision: input.revision }));

export interface DeleteDraftInput {
  readonly perform: () => boolean;
}
export const deleteDraftActor = fromPromise<boolean, DeleteDraftInput>(async ({ input }) => input.perform());
