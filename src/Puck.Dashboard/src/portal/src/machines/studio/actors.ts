/**
 * The studio machine's outside work, one invoked actor each: engine boots, opening a workspace, the compiled view,
 * geometry, the preview's engine calls, draft writes, and the two streams that run while a workspace is open.
 * Promise rejections become `onError` transitions. The streams are the machine's RxJS seam: each is a
 * `fromEventObservable` actor whose events (DIAGNOSTICS, ISLAND_CHECKED) the machine records with pure `assign`.
 */
import { type AnyActorRef, fromCallback, fromEventObservable, fromPromise } from "xstate";
import { catchError, distinctUntilChanged, EMPTY, filter, map, Observable, of } from "rxjs";
import { islandChecks, type IslandRequest } from "./islandCheck";
import { sourceDiagnostics } from "./sourceDiagnostics";
import { checkWorkspace, openWorkspace, revisionClean, workspaceTexts, writeChangedSources, type OpenedWorkspace } from "./workspace";
import { computeGeometry, type GeometryOutcome } from "./geometry";
import { compilePreview, releasePreview, replayPreview, tickPreview, writePreviewRow, type PreviewOutcome } from "./preview";
import type { IslandCheck, PreviewScriptStep, PreviewSnapshot, StudioContext, StudioEvent, StudioMachineInput, WorkspaceState } from "./types";
import { readDraftListing, type DraftListing, type LocalDraftStore } from "../../document/localDrafts";
import { loadOfficial, type OfficialLoad } from "../../official/officialClient";
import type { LanguageServerChannel, RowInfo, SourceCompileResult, WorldEngine } from "../../native/engineTypes";

const message = (error: unknown) => (error instanceof Error ? error.message : String(error));

/** The loaded official build, and the language engine with its language server, or why that engine refused to boot. */
export type BootOutput =
  | { readonly officialLoad: OfficialLoad; readonly engine: WorldEngine; readonly channel: LanguageServerChannel; readonly languageRefusal: null }
  | { readonly officialLoad: OfficialLoad; readonly engine: null; readonly channel: null; readonly languageRefusal: string };
/** Loads the official build and boots the language engine from its verified bytes, then opens its language server.
 * An official build that does not load refuses the boot; a language engine that does not boot is an answer, so the
 * workspace still opens without it. */
export const bootActor = fromPromise<BootOutput, StudioMachineInput>(async ({ input, signal }) => {
  try {
    const officialLoad = await loadOfficial(input.official, input.fetchImpl, input.byteStore);
    signal.throwIfAborted();
    let engine: WorldEngine | null = null;
    try {
      engine = await input.bootEngine(officialLoad, { mode: input.engineMode, fetchImpl: input.fetchImpl, signal });
      signal.throwIfAborted();
      // The engine's first answer: an engine that cannot give it did not boot.
      await engine.version();
      signal.throwIfAborted();
      return { officialLoad, engine, channel: engine.languageServer(), languageRefusal: null };
    } catch (error) {
      await engine?.dispose();
      if (signal.aborted) throw error;
      return { officialLoad, engine: null, channel: null, languageRefusal: message(error) };
    }
  } catch (error) {
    // @xstate/react rehydrates stopped actors during StrictMode effect replay. An old
    // cancelled promise must not deliver its rejection to that actor's new invocation.
    if (signal.aborted) return new Promise<never>(() => {});
    throw error;
  }
});

export interface WorldBootInput {
  readonly official: OfficialLoad;
  readonly machineInput: StudioMachineInput;
  /** The language engine's compiled module, when it has one: the world engine instantiates it rather than compiling. */
  readonly wasmModule?: WebAssembly.Module;
}
/** Boots the world engine from the same verified official bytes as the language engine, and from its compiled module. */
export const worldBootActor = fromPromise<WorldEngine, WorldBootInput>(async ({ input, signal }) => {
  const engine = await input.machineInput.bootEngine(input.official, {
    mode: input.machineInput.engineMode, fetchImpl: input.machineInput.fetchImpl, signal, wasmModule: input.wasmModule,
  });
  if (signal.aborted) {
    await engine.dispose();
    return new Promise<never>(() => {});
  }
  return engine;
});

/** Where an opened workspace comes from: an official document, or a draft over the official build. */
export type OpenInput =
  | { readonly source: "official"; readonly official: OfficialLoad | null; readonly engine: WorldEngine | null; readonly name: string }
  | { readonly source: "draft"; readonly official: OfficialLoad | null; readonly engine: WorldEngine | null; readonly store: LocalDraftStore; readonly id: string };
export interface OpenOutput {
  readonly workspace: OpenedWorkspace;
  /** Why the language engine could not mount the workspace, when it could not. */
  readonly mountRefusal: string | null;
}
export const openActor = fromPromise<OpenOutput, OpenInput>(async ({ input }) => {
  if (!input.official) throw new Error("cannot open a document before the official build has loaded.");
  let workspace: OpenedWorkspace;
  if (input.source === "draft") {
    const draft = input.store.load(input.id);
    if (!draft) throw new Error(`no local draft named '${input.id}'.`);
    workspace = await openWorkspace(input.official, draft.documentName, draft);
  } else {
    workspace = await openWorkspace(input.official, input.name, null);
  }
  if (!input.engine) return { workspace, mountRefusal: "the language engine is not running, so the source has no diagnostics." };
  try {
    await input.engine.mountSources(workspaceTexts(workspace.files));
    return { workspace, mountRefusal: null };
  } catch (error) {
    return { workspace, mountRefusal: `the language engine could not mount the workspace: ${message(error)}` };
  }
});

export interface CompiledInput {
  readonly engine: WorldEngine;
  readonly workspace: WorkspaceState;
}
export interface CompiledOutput {
  readonly workspaceId: number;
  readonly path: string;
  readonly revision: number;
  readonly result: SourceCompileResult;
}
/** Compiles the open source on the language engine, after writing the editor's changes into it. */
export const compiledActor = fromPromise<CompiledOutput, CompiledInput>(async ({ input }) => {
  await writeChangedSources(input.engine, input.workspace);
  const result = await input.engine.compileSource(input.workspace.active);
  return { workspaceId: input.workspace.id, path: input.workspace.active, revision: input.workspace.revision, result };
});

/** The language server's diagnostics, as DIAGNOSTICS events, for as long as a workspace is open. A failed channel
 * becomes one LANGUAGE_FAILED event rather than an error the machine must catch. */
export const diagnosticsActor = fromEventObservable<StudioEvent, { channel: LanguageServerChannel | null }>(({ input }) =>
  input.channel
    ? sourceDiagnostics(input.channel.messages()).pipe(
      map((published): StudioEvent => ({ type: "DIAGNOSTICS", path: published.path, version: published.version, diagnostics: published.diagnostics })),
      catchError((error) => of<StudioEvent>({ type: "LANGUAGE_FAILED", message: message(error) })),
    )
    : EMPTY);

/** The parent machine's snapshots, starting with the current one. */
function snapshots(parent: AnyActorRef): Observable<{ context: StudioContext }> {
  return new Observable((subscriber) => {
    subscriber.next(parent.getSnapshot());
    const subscription = parent.subscribe({ next: (snapshot) => subscriber.next(snapshot) });
    return () => subscription.unsubscribe();
  });
}

/** Island checks, as ISLAND_CHECKED events, for as long as a workspace is open. The requests follow the machine's
 * own context: a revision is ready once it is diagnosed clean, the workspace has a root, and the world engine runs. */
export const islandActor = fromEventObservable<StudioEvent, { parent: AnyActorRef }>(({ input }) => {
  const requests$ = snapshots(input.parent).pipe(
    map(({ context }): IslandRequest<Omit<IslandCheck, "value">> | null => {
      const workspace = context.workspace;
      if (!workspace) return null;
      const engine = context.worldEngine;
      const root = workspace.root;
      const ready = engine !== null && root !== null && revisionClean(workspace);
      return { revision: workspace.revision, run: ready ? () => checkWorkspace(engine, workspace, root) : null };
    }),
    filter((request): request is IslandRequest<Omit<IslandCheck, "value">> => request !== null),
    distinctUntilChanged((left, right) => left.revision === right.revision && (left.run === null) === (right.run === null)),
  );
  return islandChecks(requests$).pipe(
    map(({ outcome }): StudioEvent => ({ type: "ISLAND_CHECKED", check: outcome })),
    catchError((error) => of<StudioEvent>({ type: "WORLD_FAILED", message: message(error) })),
  );
});

export interface GeometryInput {
  readonly engine: WorldEngine;
  readonly value: unknown;
}
export const geometryActor = fromPromise<GeometryOutcome, GeometryInput>(async ({ input }) =>
  computeGeometry(input.engine, input.value));

export interface CompileInput {
  readonly engine: WorldEngine;
  readonly composed: string;
}
export type CompileOutput = PreviewOutcome & { readonly snapshot: PreviewSnapshot | null };
export const compileActor = fromPromise<CompileOutput, CompileInput>(async ({ input, signal }) => compilePreview(input.engine, input.composed, signal));

/** Releases the language channel and engine when the studio stops. */
export const sessionLifetimeActor = fromCallback<{ type: "unused" }, { engine: WorldEngine | null; channel: LanguageServerChannel | null }>(({ input }) =>
  () => {
    input.channel?.close();
    void input.engine?.dispose().catch(() => undefined);
  });

/** Releases the accepted preview handle and the world engine when the studio stops. */
export const worldLifetimeActor = fromCallback<{ type: "unused" }, { engine: WorldEngine; handle: () => string | null }>(({ input }) =>
  () => {
    void releasePreview(input.engine, input.handle()).finally(() => input.engine.dispose().catch(() => undefined));
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
  readonly composed: string;
  readonly script: readonly PreviewScriptStep[];
  readonly upTo: number;
  readonly index: number;
  readonly expectedHash: string;
}
export type ReplayOutput = PreviewOutcome & { readonly index: number };
export const replayActor = fromPromise<ReplayOutput, ReplayInput>(async ({ input, signal }) => {
  const outcome = await replayPreview(input.engine, input.composed, input.script, input.upTo, signal, input.expectedHash);
  return { ...outcome, index: input.index };
});

export interface SaveDraftInput {
  readonly store: LocalDraftStore;
  readonly id: string;
  readonly title: string;
  readonly documentName: string;
  readonly files: Readonly<Record<string, string>>;
}
/** The library as it stands after the save, and the id the draft was saved under. */
export interface SaveDraftOutput {
  readonly id: string;
  readonly drafts: DraftListing;
}
export const saveDraftActor = fromPromise<SaveDraftOutput, SaveDraftInput>(async ({ input }) => {
  const changed = Object.keys(input.files).length;
  input.store.save(input.id, input.title, input.documentName, input.files, `${changed} changed file${changed === 1 ? "" : "s"}`);
  return { id: input.id, drafts: readDraftListing(input.store) };
});

export interface DeleteDraftInput {
  readonly store: LocalDraftStore;
  readonly id: string;
}
export const deleteDraftActor = fromPromise<DraftListing, DeleteDraftInput>(async ({ input }) => {
  input.store.delete(input.id);
  return readDraftListing(input.store);
});
