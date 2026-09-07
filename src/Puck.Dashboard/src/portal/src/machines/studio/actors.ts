/**
 * Async engine/store operations and session lifetime actors for the studio statechart.
 * Promise rejections become onError transitions; local edit reducers are handled synchronously
 * by the machine. These actors have no dependency on the machine's setup builder.
 */
import { fromCallback, fromPromise } from "xstate";
import { validateDocument, type ValidationOutcome } from "./document";
import { computeGeometry, type GeometryOutcome } from "./geometry";
import { compilePreview, releasePreview, replayPreview, tickPreview, writePreviewRow, type PreviewOutcome } from "./preview";
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
export const bootActor = fromPromise<BootOutput, StudioMachineInput>(async ({ input, signal }) => {
  let engine: WorldEngine | null = null;
  try {
    const officialLoad = await loadOfficial(input.official, input.fetchImpl, input.byteStore);
    signal.throwIfAborted();
    engine = await input.bootEngine(officialLoad, { mode: input.engineMode, fetchImpl: input.fetchImpl, signal });
    signal.throwIfAborted();
    const version = await engine.version();
    signal.throwIfAborted();
    return { officialLoad, engine, version };
  } catch (error) {
    await engine?.dispose();
    // @xstate/react rehydrates stopped actors during StrictMode effect replay. An old
    // cancelled promise must not deliver its rejection to that actor's new invocation.
    if (signal.aborted) return new Promise<never>(() => {});
    throw error;
  }
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
export const compileActor = fromPromise<CompileOutput, CompileInput>(async ({ input, signal }) => compilePreview(input.engine, input.sourceJson, signal));

/** Releases the accepted preview handle and its engine when the studio actor stops. */
export const sessionLifetimeActor = fromCallback<{ type: "unused" }, { engine: WorldEngine | null; handle: () => string | null }>(({ input }) =>
  () => {
    if (!input.engine) return;
    void releasePreview(input.engine, input.handle());
    void input.engine.dispose().catch(() => undefined);
  });

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
  readonly expectedHash: string;
}
export type ReplayOutput = PreviewOutcome & { readonly index: number };
export const replayActor = fromPromise<ReplayOutput, ReplayInput>(async ({ input, signal }) => {
  const outcome = await replayPreview(input.engine, input.sourceJson, input.script, input.upTo, signal, input.expectedHash);
  return { ...outcome, index: input.index };
});

export interface SaveDraftInput {
  readonly perform: () => StudioDraft;
  readonly text: string;
}
export type SaveDraftOutput = { readonly draft: StudioDraft; readonly text: string };
export const saveDraftActor = fromPromise<SaveDraftOutput, SaveDraftInput>(async ({ input }) => ({ draft: input.perform(), text: input.text }));

export interface DeleteDraftInput {
  readonly perform: () => boolean;
}
export const deleteDraftActor = fromPromise<boolean, DeleteDraftInput>(async ({ input }) => input.perform());
