/**
 * The studio: one machine over a `.puck` source workspace and the two engines that serve it.
 *
 * The language engine boots with the studio and hosts the language server; the world engine boots the first time a
 * revision is ready for an island check, and hosts composition, the preview, the console, and geometry. Both boot
 * from the same verified official bytes. Opening a document reads the official build's `sources[]` (with a draft's
 * files laid over it), mounts them into the language engine, and opens the document's own source; the world engine
 * receives the workspace lazily, before its next job.
 *
 * CodeMirror owns each file's buffer and undo history. The editor reports every change as SOURCE_CHANGED and the
 * machine records the text and version with pure `assign`. Two RxJS streams run as invoked actors while a workspace
 * is open and feed the machine events: the language server's source-tier diagnostics (DIAGNOSTICS) and the world
 * engine's island check (ISLAND_CHECKED), which compiles the open source in full — the semantic tier, with its IR
 * and source map — and composes the workspace's root. The `checking` tag answers "source-tier diagnostics are
 * pending for the current version". A preview starts only when the current revision is clean at both tiers and its
 * root composed; it compiles that composition on the world engine, and a newer source change stops it.
 *
 * Every read or write outside the machine is an invoked actor, so its failure is a transition and the state names
 * what is in flight. States the UI treats as "wait" carry the `preview-busy` tag.
 */
import { assertEvent, assign, setup } from "xstate";
import { branchPreview, releasePreview } from "./studio/preview";
import {
  bootActor,
  compileActor,
  compiledActor,
  deleteDraftActor,
  diagnosticsActor,
  geometryActor,
  islandActor,
  openActor,
  replayActor,
  saveDraftActor,
  sessionLifetimeActor,
  tickActor,
  worldBootActor,
  worldLifetimeActor,
  writeActor,
  type CompileInput,
  type CompileOutput,
  type CompiledInput,
  type DeleteDraftInput,
  type GeometryInput,
  type OpenInput,
  type ReplayInput,
  type SaveDraftInput,
  type TickInput,
  type WorldBootInput,
  type WriteInput,
} from "./studio/actors";
import {
  acceptIsland,
  diagnosesCurrent,
  draftFiles,
  islandClean,
  revisionClean,
} from "./studio/workspace";
import {
  OPEN_EVENTS,
  type PreviewScriptStep,
  type PreviewState,
  type SelectionState,
  type StudioContext,
  type StudioEvent,
  type StudioMachineInput,
  type WorkspaceState,
} from "./studio/types";
import { defaultLocalDraftStore, readDraftListing } from "../document/localDrafts";
import { parseDocumentText } from "../document/jsonText";
import { isPuckSource } from "../document/sourcePaths";

function initialPreview(): PreviewState {
  return {
    handle: null,
    tick: 0n,
    rows: [],
    snapshots: [],
    cursor: 0,
    refusals: [],
    status: "idle",
    refusal: null,
    script: [],
    composed: null,
  };
}

const clearedSelection: SelectionState = { topology: null, ordinals: [], hovered: null };

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function slugifyDraftId(name: string): string {
  const slug = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
  return (slug.length > 0 && /^[a-z0-9]/.test(slug)) ? slug : `draft-${slug.length > 0 ? slug : "untitled"}`;
}

/** Why no preview can start whatever the source does — the official build or an engine the preview needs refused to
 * boot — or `null` when nothing is known to stand in the way. */
export function engineRefusal(context: StudioContext): string | null {
  if (context.boot.status === "refused") return `the official build did not load: ${context.boot.refusal ?? "unknown reason."}`;
  if (context.language.status === "refused") return `the language engine refused to boot: ${context.language.refusal ?? "unknown reason."}`;
  if (context.world.status === "refused") return `the world engine refused to boot: ${context.world.refusal ?? "unknown reason."}`;
  return null;
}

/** Why a preview may not start now, or `null` when it may. */
export function previewRefusal(context: StudioContext): string | null {
  const known = engineRefusal(context);
  if (known) return known;
  const workspace = context.workspace;
  if (!workspace) return "open a document before starting a preview.";
  if (!workspace.root) return workspace.rootRefusal ?? "this document has nothing the studio can compose.";
  if (!diagnosesCurrent(workspace)) return "the source is still being checked; start the preview once its diagnostics arrive.";
  if (!revisionClean(workspace)) return "the source has errors; fix them before starting a preview.";
  if (!islandClean(workspace)) {
    return (workspace.island?.revision === workspace.revision)
      ? "the world engine's check found errors in these edits; fix them first."
      : "the world engine's check has not finished for this revision yet.";
  }
  return context.worldEngine ? null : "the world engine is not running yet.";
}

/** The workspace, which a guard has already established is open. */
function workspaceOf(context: StudioContext): WorkspaceState {
  return context.workspace!;
}

export const studioMachine = setup({
  types: {
    context: {} as StudioContext,
    events: {} as StudioEvent,
    input: {} as StudioMachineInput,
    tags: {} as "preview-busy" | "checking",
  },
  actors: {
    bootActor,
    worldBootActor,
    openActor,
    compiledActor,
    diagnosticsActor,
    islandActor,
    geometryActor,
    compileActor,
    writeActor,
    tickActor,
    replayActor,
    saveDraftActor,
    deleteDraftActor,
    sessionLifetimeActor,
    worldLifetimeActor,
  },
  guards: {
    hasWorkspace: ({ context }) => context.workspace !== null,
    islandCheckDue: ({ context }) =>
      context.official !== null && !!context.workspace?.root && revisionClean(context.workspace),
    // Without a language engine no diagnostics will arrive, so the open file is not left checking.
    diagnosisSettles: ({ context }) => !!context.workspace && (context.languageEngine === null || diagnosesCurrent(context.workspace)),
    previewAllowed: ({ context }) => previewRefusal(context) === null,
    // A start that is known to fail is not taken, so it never looks actionable; the reason shows without a press.
    enginesUsable: ({ context }) => engineRefusal(context) === null,
    fileExists: ({ context, event }) => {
      assertEvent(event, ["OPEN_FILE", "REVEAL_SOURCE"]);
      return !!context.workspace && Object.hasOwn(context.workspace.files, event.path);
    },
    sourceChangeApplies: ({ context, event }) => {
      assertEvent(event, "SOURCE_CHANGED");
      const file = context.workspace?.files[event.path];
      return !!file && isPuckSource(event.path) && event.version > file.version && event.text !== file.text;
    },
    islandCurrent: ({ context, event }) => {
      assertEvent(event, "ISLAND_CHECKED");
      return context.workspace?.revision === event.check.revision;
    },
    compiledStale: ({ context }) => {
      const workspace = context.workspace;
      const compiled = context.compiled;
      return !!workspace && context.languageEngine !== null && !(compiled && compiled.workspaceId === workspace.id
        && compiled.path === workspace.active && compiled.revision === workspace.revision);
    },
    canSaveDraft: ({ context }) => context.workspace !== null,
    canStepBack: ({ context }) => context.preview.cursor > 0,
    canStepForward: ({ context }) => context.preview.cursor < context.preview.snapshots.length - 1,
    jumpMovesCursor: ({ context, event }) => {
      assertEvent(event, "JUMP_TO_TICK");
      return Number.isInteger(event.index) && event.index !== context.preview.cursor
        && event.index >= 0 && event.index < context.preview.snapshots.length;
    },
  },
  actions: {
    refuse: assign({
      refusals: ({ context }, params: { message: string }) => [...context.refusals, params.message],
    }),
    acceptIsland: assign(({ context, event }) => {
      assertEvent(event, "ISLAND_CHECKED");
      const workspace = workspaceOf(context);
      const island = acceptIsland(event.check);
      const compile = island.compile;
      return {
        workspace: { ...workspace, island, composition: island.ok ? island.value : workspace.composition },
        // The semantic tier's compile is the open source's compiled view at this revision, so showing it costs nothing.
        compiled: compile ? {
          workspaceId: workspace.id,
          path: compile.path,
          revision: island.revision,
          result: compile.result,
          value: compile.result.document === null ? null : parseDocumentText(compile.result.document),
          sourceMap: compile.result.sourceMap,
        } : context.compiled,
      };
    }),
    releasePreviewHandle: ({ context }) => {
      if (context.worldEngine && context.preview.handle) {
        void releasePreview(context.worldEngine, context.preview.handle);
      }
    },
    invalidatePreview: assign({
      preview: () => initialPreview(),
    }),
    acceptCompiledPreview: assign({
      preview: (_, params: { output: CompileOutput; composed: string }) => ({
        ...initialPreview(),
        status: "ready" as const,
        handle: params.output.handle,
        rows: params.output.rows,
        snapshots: params.output.snapshot ? [params.output.snapshot] : [],
        composed: params.composed,
      }),
    }),
    refusePreview: assign({
      preview: (_, params: { refusal: string }) => ({ ...initialPreview(), status: "refused" as const, refusal: params.refusal }),
    }),
    recordPreviewRefusal: assign({
      preview: ({ context }, params: { refusal: string }) => ({ ...context.preview, refusals: [...context.preview.refusals, params.refusal] }),
    }),
  },
}).createMachine({
  id: "studio",
  context: ({ input }) => {
    const draftStore = input.draftStore ?? defaultLocalDraftStore;
    return {
      official: null,
      languageEngine: null,
      languageChannel: null,
      worldEngine: null,
      boot: { status: "booting", refusal: null, build: null },
      language: { status: "booting", refusal: null },
      world: { status: "dormant", refusal: null },
      workspace: null,
      compiled: null,
      geometry: {},
      selection: clearedSelection,
      preview: initialPreview(),
      drafts: readDraftListing(draftStore),
      refusals: [],
      machineInput: { ...input, draftStore },
    };
  },
  initial: "booting",
  states: {
    booting: {
      invoke: {
        src: "bootActor",
        input: ({ context }) => context.machineInput,
        onDone: {
          target: "ready",
          actions: assign({
            official: ({ event }) => event.output.officialLoad,
            languageEngine: ({ event }) => event.output.engine,
            languageChannel: ({ event }) => event.output.channel,
            boot: ({ event }) => ({
              status: "ready" as const,
              refusal: null,
              build: {
                commit: event.output.officialLoad.build.commit,
                dirty: event.output.officialLoad.build.dirty,
                schemaVersion: event.output.officialLoad.build.worldSchema,
                source: event.output.officialLoad.source,
              },
            }),
            language: ({ event }) => (event.output.engine
              ? { status: "ready" as const, refusal: null }
              : { status: "refused" as const, refusal: event.output.languageRefusal }),
          }),
        },
        onError: {
          target: "ready",
          actions: assign({
            boot: ({ event }) => ({ status: "refused" as const, refusal: errorMessage(event.error), build: null }),
            language: { status: "refused" as const, refusal: "the official build it boots from did not load." },
          }),
        },
      },
    },
    ready: {
      type: "parallel",
      invoke: { src: "sessionLifetimeActor", input: ({ context }) => ({ engine: context.languageEngine, channel: context.languageChannel }) },
      on: {
        SELECT_TOPOLOGY: {
          actions: assign({ selection: ({ context, event }) => ({ ...context.selection, topology: event.name, ordinals: [], hovered: null }) }),
        },
        SELECT_CELLS: {
          actions: assign({
            selection: ({ context, event }) => ({
              ...context.selection,
              ordinals: (event.mode === "replace"
                ? event.ordinals
                : [
                  ...context.selection.ordinals.filter((ordinal) => !event.ordinals.includes(ordinal)),
                  ...event.ordinals.filter((ordinal) => !context.selection.ordinals.includes(ordinal)),
                ]),
            }),
          }),
        },
        HOVER_CELL: {
          actions: assign({ selection: ({ context, event }) => ({ ...context.selection, hovered: event.ordinal }) }),
        },
        LANGUAGE_FAILED: {
          actions: { type: "refuse", params: ({ event }) => ({ message: `the language server stopped: ${event.message}` }) },
        },
        WORLD_FAILED: {
          actions: { type: "refuse", params: ({ event }) => ({ message: `the island check failed: ${event.message}` }) },
        },
      },
      states: {
        world: {
          initial: "dormant",
          states: {
            dormant: {
              always: { guard: "islandCheckDue", target: "booting" },
            },
            booting: {
              entry: assign({ world: { status: "booting", refusal: null } }),
              invoke: {
                src: "worldBootActor",
                input: ({ context }): WorldBootInput => ({ official: context.official!, machineInput: context.machineInput, wasmModule: context.languageEngine?.wasmModule }),
                onDone: {
                  target: "ready",
                  actions: assign({ worldEngine: ({ event }) => event.output, world: { status: "ready", refusal: null } }),
                },
                onError: {
                  target: "refused",
                  actions: assign({ world: ({ event }) => ({ status: "refused" as const, refusal: errorMessage(event.error) }) }),
                },
              },
            },
            ready: {
              invoke: {
                src: "worldLifetimeActor",
                input: ({ context, self }) => ({ engine: context.worldEngine!, handle: () => self.getSnapshot().context.preview.handle }),
              },
            },
            refused: {},
          },
        },
        library: {
          initial: "idle",
          states: {
            idle: {
              on: {
                SAVE_DRAFT: { guard: "canSaveDraft", target: "saving" },
                DELETE_DRAFT: { target: "deleting" },
              },
            },
            saving: {
              invoke: {
                src: "saveDraftActor",
                input: ({ context, event }): SaveDraftInput => {
                  assertEvent(event, "SAVE_DRAFT");
                  const workspace = workspaceOf(context);
                  const id = event.id ?? workspace.draftId ?? slugifyDraftId(event.title ?? workspace.documentName);
                  return {
                    store: context.machineInput.draftStore,
                    id,
                    title: event.title ?? id,
                    documentName: workspace.documentName,
                    files: draftFiles(workspace.files),
                  };
                },
                onDone: {
                  target: "idle",
                  actions: assign({
                    workspace: ({ context, event }) => context.workspace && {
                      ...context.workspace,
                      draftId: event.output.id,
                      files: Object.fromEntries(Object.entries(context.workspace.files).map(([path, file]) => [path, { ...file, savedText: file.text }])),
                    },
                    drafts: ({ event }) => event.output.drafts,
                  }),
                },
                onError: { target: "idle", actions: { type: "refuse", params: ({ event }) => ({ message: `the draft was not saved: ${errorMessage(event.error)}` }) } },
              },
            },
            deleting: {
              invoke: {
                src: "deleteDraftActor",
                input: ({ context, event }): DeleteDraftInput => {
                  assertEvent(event, "DELETE_DRAFT");
                  return { store: context.machineInput.draftStore, id: event.id };
                },
                onDone: { target: "idle", actions: assign({ drafts: ({ event }) => event.output }) },
                onError: { target: "idle", actions: { type: "refuse", params: ({ event }) => ({ message: `the draft was not deleted: ${errorMessage(event.error)}` }) } },
              },
            },
          },
        },
        workspace: {
          initial: "empty",
          on: {
            OPEN_OFFICIAL: { target: ".opening" },
            LOAD_DRAFT: { target: ".opening" },
          },
          states: {
            empty: {},
            // A newer open replaces one still reading: the older read is cancelled with its state.
            opening: {
              on: {
                OPEN_OFFICIAL: { target: "opening", reenter: true },
                LOAD_DRAFT: { target: "opening", reenter: true },
              },
              invoke: {
                src: "openActor",
                input: ({ context, event }): OpenInput => {
                  assertEvent(event, [...OPEN_EVENTS]);
                  return event.type === "OPEN_OFFICIAL"
                    ? { source: "official", official: context.official, engine: context.languageEngine, name: event.name }
                    : { source: "draft", official: context.official, engine: context.languageEngine, store: context.machineInput.draftStore, id: event.id };
                },
                onDone: {
                  target: "open",
                  actions: assign({
                    workspace: ({ context, event }) => ({
                      ...event.output.workspace,
                      id: (context.workspace?.id ?? 0) + 1,
                      revision: 0,
                      diagnostics: {},
                      island: null,
                      composition: null,
                      reveal: null,
                    }),
                    compiled: null,
                    geometry: {},
                    selection: clearedSelection,
                    refusals: ({ event }) => (event.output.mountRefusal ? [event.output.mountRefusal] : []),
                  }),
                },
                onError: [
                  { guard: "hasWorkspace", target: "open", actions: { type: "refuse", params: ({ event }) => ({ message: errorMessage(event.error) }) } },
                  { target: "empty", actions: { type: "refuse", params: ({ event }) => ({ message: errorMessage(event.error) }) } },
                ],
              },
            },
            open: {
              type: "parallel",
              invoke: [
                { src: "diagnosticsActor", input: ({ context }) => ({ channel: context.languageChannel }) },
                { src: "islandActor", input: ({ self }) => ({ parent: self }) },
              ],
              on: {
                OPEN_FILE: {
                  guard: "fileExists",
                  target: ".diagnosis.checking",
                  actions: assign({ workspace: ({ context, event }) => ({ ...workspaceOf(context), active: event.path }) }),
                },
                SOURCE_CHANGED: {
                  guard: "sourceChangeApplies",
                  target: ".diagnosis.checking",
                  actions: assign({
                    workspace: ({ context, event }) => {
                      const workspace = workspaceOf(context);
                      const file = workspace.files[event.path];
                      return {
                        ...workspace,
                        revision: workspace.revision + 1,
                        files: { ...workspace.files, [event.path]: { ...file, text: event.text, version: event.version } },
                      };
                    },
                  }),
                },
                // A publication without a version clears a document the editor closed; the file's diagnostics still
                // describe its text, so the machine keeps them.
                DIAGNOSTICS: {
                  guard: ({ event }) => event.version !== null,
                  actions: assign({
                    workspace: ({ context, event }) => {
                      const workspace = workspaceOf(context);
                      return { ...workspace, diagnostics: { ...workspace.diagnostics, [event.path]: { version: event.version!, items: event.diagnostics } } };
                    },
                  }),
                },
                // A clean composition lays out its topologies again; a refused one keeps the last layout.
                ISLAND_CHECKED: [
                  {
                    guard: ({ context, event }) => context.workspace?.revision === event.check.revision && event.check.ok,
                    target: ".layout.computing",
                    actions: "acceptIsland",
                  },
                  { guard: "islandCurrent", actions: "acceptIsland" },
                ],
                REVEAL_SOURCE: {
                  guard: "fileExists",
                  target: ".diagnosis.checking",
                  actions: assign({
                    workspace: ({ context, event }) => {
                      const workspace = workspaceOf(context);
                      return {
                        ...workspace,
                        active: event.path,
                        reveal: { path: event.path, line: event.line, column: event.column, length: event.length, nonce: (workspace.reveal?.nonce ?? 0) + 1 },
                      };
                    },
                  }),
                },
                REQUEST_COMPILED: { guard: "compiledStale", target: ".compiled.compiling" },
              },
              states: {
                diagnosis: {
                  initial: "checking",
                  states: {
                    checking: {
                      tags: "checking",
                      always: { guard: "diagnosisSettles", target: "settled" },
                    },
                    settled: {},
                  },
                },
                layout: {
                  initial: "idle",
                  states: {
                    idle: {},
                    computing: {
                      invoke: {
                        src: "geometryActor",
                        input: ({ context }): GeometryInput => ({ engine: context.worldEngine!, value: workspaceOf(context).composition }),
                        onDone: {
                          target: "idle",
                          actions: assign({
                            geometry: ({ event }) => event.output.geometry,
                            selection: ({ context, event }) => {
                              const geometry = event.output.geometry;
                              const topology = context.selection.topology && Object.hasOwn(geometry, context.selection.topology)
                                ? context.selection.topology : Object.keys(geometry)[0] ?? null;
                              if (topology && topology === context.selection.topology && geometry[topology] === context.geometry[topology]) return context.selection;
                              const valid = new Set(topology ? geometry[topology].map(cell => cell.ordinal) : []);
                              return { topology, ordinals: topology === context.selection.topology ? context.selection.ordinals.filter(ordinal => valid.has(ordinal)) : [], hovered: null };
                            },
                            refusals: ({ context, event }) => (event.output.diagnostics.length === 0
                              ? context.refusals
                              : [...context.refusals, ...event.output.diagnostics.map((diagnostic) => `${diagnostic.path}: ${diagnostic.message}`)]),
                          }),
                        },
                        onError: {
                          target: "idle",
                          actions: { type: "refuse", params: ({ event }) => ({ message: `geometry: ${errorMessage(event.error)}` }) },
                        },
                      },
                    },
                  },
                },
                compiled: {
                  initial: "idle",
                  states: {
                    idle: {},
                    compiling: {
                      invoke: {
                        src: "compiledActor",
                        input: ({ context }): CompiledInput => ({ engine: context.languageEngine!, workspace: workspaceOf(context) }),
                        onDone: {
                          target: "idle",
                          actions: assign({
                            compiled: ({ event }) => ({
                              ...event.output,
                              value: event.output.result.document === null ? null : parseDocumentText(event.output.result.document),
                              sourceMap: event.output.result.sourceMap,
                            }),
                          }),
                        },
                        onError: {
                          target: "idle",
                          actions: { type: "refuse", params: ({ event }) => ({ message: `the source did not compile: ${errorMessage(event.error)}` }) },
                        },
                      },
                    },
                  },
                },
              },
            },
          },
        },
        preview: {
          initial: "idle",
          // Applies in every child state below that doesn't declare its own handler for these types — the one place
          // "a newer source stops the preview" is implemented. A source change the workspace does not take starts no
          // revision, so the preview keeps running.
          on: {
            SOURCE_CHANGED: { guard: "sourceChangeApplies", target: ".idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
            OPEN_OFFICIAL: { target: ".idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
            LOAD_DRAFT: { target: ".idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
            PREVIEW_STOP: { target: ".idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
          },
          states: {
            idle: {
              on: {
                PREVIEW_START: [
                  { guard: "previewAllowed", target: "compiling" },
                  {
                    guard: "enginesUsable",
                    target: "refused",
                    actions: { type: "refusePreview", params: ({ context }) => ({ refusal: previewRefusal(context) ?? "the preview was refused." }) },
                  },
                ],
              },
            },
            refused: {
              on: {
                PREVIEW_START: { guard: "previewAllowed", target: "compiling" },
              },
            },
            compiling: {
              tags: "preview-busy",
              invoke: {
                src: "compileActor",
                input: ({ context }): CompileInput => ({ engine: context.worldEngine!, composed: workspaceOf(context).island!.composed! }),
                onDone: [
                  {
                    guard: ({ event }) => event.output.status === "ready",
                    target: "ready",
                    actions: { type: "acceptCompiledPreview", params: ({ context, event }) => ({ output: event.output, composed: workspaceOf(context).island!.composed! }) },
                  },
                  {
                    target: "refused",
                    actions: { type: "refusePreview", params: ({ event }) => ({ refusal: event.output.refusal ?? "Preview compile refused." }) },
                  },
                ],
                onError: {
                  target: "refused",
                  actions: { type: "refusePreview", params: ({ event }) => ({ refusal: errorMessage(event.error) }) },
                },
              },
            },
            ready: {
              on: {
                PREVIEW_WRITE: { target: "writing" },
                PREVIEW_TICK: { target: "ticking" },
                PREVIEW_UNDO: { guard: "canStepBack", target: "restoring" },
                PREVIEW_REDO: { guard: "canStepForward", target: "restoring" },
                JUMP_TO_TICK: { guard: "jumpMovesCursor", target: "restoring" },
                RESET_WORLD: { target: "resetting" },
              },
            },
            writing: {
              tags: "preview-busy",
              invoke: {
                src: "writeActor",
                input: ({ context, event }): WriteInput => {
                  assertEvent(event, "PREVIEW_WRITE");
                  return { engine: context.worldEngine!, handle: context.preview.handle!, row: event.row, key: event.key, value: event.value, write: event.write };
                },
                onDone: {
                  target: "ready",
                  actions: assign({
                    preview: ({ context, event }) => {
                      if (!event.output.ok) return { ...context.preview, refusals: [...context.preview.refusals, event.output.error] };
                      const preview = branchPreview(context.preview);
                      return { ...preview, rows: event.output.rows, script: [...preview.script, event.output.step] };
                    },
                  }),
                },
                onError: {
                  target: "ready",
                  actions: { type: "recordPreviewRefusal", params: ({ event }) => ({ refusal: errorMessage(event.error) }) },
                },
              },
            },
            ticking: {
              tags: "preview-busy",
              invoke: {
                src: "tickActor",
                input: ({ context }): TickInput => ({
                  engine: context.worldEngine!,
                  handle: context.preview.handle!,
                  nextTick: context.preview.tick + 1n,
                  scriptLength: branchPreview(context.preview).script.length,
                }),
                onDone: {
                  target: "ready",
                  actions: assign({
                    preview: ({ context, event }) => {
                      if (!event.output.ok) return { ...context.preview, refusals: [...context.preview.refusals, event.output.error] };
                      const snapshot = event.output.snapshot;
                      const preview = branchPreview(context.preview);
                      const script: PreviewScriptStep[] = [...preview.script, { kind: "tick", tick: snapshot.tick }];
                      return {
                        ...preview,
                        tick: snapshot.tick,
                        rows: snapshot.rows,
                        snapshots: [...preview.snapshots, snapshot],
                        cursor: preview.snapshots.length,
                        script,
                      };
                    },
                  }),
                },
                onError: {
                  target: "ready",
                  actions: { type: "recordPreviewRefusal", params: ({ event }) => ({ refusal: errorMessage(event.error) }) },
                },
              },
            },
            restoring: {
              tags: "preview-busy",
              invoke: {
                src: "replayActor",
                input: ({ context, event }): ReplayInput => {
                  assertEvent(event, ["PREVIEW_UNDO", "PREVIEW_REDO", "JUMP_TO_TICK"]);
                  // The `ready` guards admit only a move to another recorded snapshot.
                  const index = (event.type === "PREVIEW_UNDO") ? context.preview.cursor - 1
                    : (event.type === "PREVIEW_REDO") ? context.preview.cursor + 1
                      : event.index;
                  return {
                    engine: context.worldEngine!,
                    composed: context.preview.composed!,
                    script: context.preview.script,
                    upTo: context.preview.snapshots[index]?.scriptLength ?? 0,
                    index,
                    expectedHash: context.preview.snapshots[index].hash,
                  };
                },
                onDone: [
                  {
                    guard: ({ event }) => event.output.status === "ready",
                    target: "ready",
                    actions: [
                      "releasePreviewHandle",
                      assign({
                        preview: ({ context, event }) => ({
                          ...context.preview,
                          handle: event.output.handle,
                          rows: event.output.rows,
                          tick: event.output.tick,
                          cursor: event.output.index,
                        }),
                      }),
                    ],
                  },
                  {
                    target: "ready",
                    actions: { type: "recordPreviewRefusal", params: ({ event }) => ({ refusal: event.output.refusal ?? "Preview restore refused." }) },
                  },
                ],
                onError: {
                  target: "ready",
                  actions: { type: "recordPreviewRefusal", params: ({ event }) => ({ refusal: errorMessage(event.error) }) },
                },
              },
            },
            resetting: {
              tags: "preview-busy",
              entry: ["releasePreviewHandle", assign({ preview: ({ context }) => ({ ...initialPreview(), composed: context.preview.composed }) })],
              invoke: {
                src: "compileActor",
                input: ({ context }): CompileInput => ({ engine: context.worldEngine!, composed: context.preview.composed! }),
                onDone: [
                  {
                    guard: ({ event }) => event.output.status === "ready",
                    target: "ready",
                    actions: { type: "acceptCompiledPreview", params: ({ context, event }) => ({ output: event.output, composed: context.preview.composed! }) },
                  },
                  {
                    target: "refused",
                    actions: { type: "refusePreview", params: ({ event }) => ({ refusal: event.output.refusal ?? "Preview compile refused." }) },
                  },
                ],
                onError: {
                  target: "refused",
                  actions: { type: "refusePreview", params: ({ event }) => ({ refusal: errorMessage(event.error) }) },
                },
              },
            },
          },
        },
      },
    },
  },
});
