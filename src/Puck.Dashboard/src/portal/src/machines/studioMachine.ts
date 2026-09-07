/**
 * The studio owns two parallel regions: applied document revisions and a hostless preview.
 * Local edits commit synchronously; validation waits for a short idle interval so typing never
 * queues a full composition per character. Applied text, JSON draft text, and saved text have
 * separate identities. Preview always compiles the applied revision.
 */
import { assign, setup } from "xstate";
import {
  applyText,
  createEmptyDocument,
  editDocument,
  openDraftDocument,
  openOfficialDocument,
  openTextDocument,
  paintCells,
  redoDocument,
  undoDocument,
} from "./studio/document";
import { branchPreview, releasePreview } from "./studio/preview";
import {
  bootActor,
  compileActor,
  deleteDraftActor,
  editActor,
  geometryActor,
  replayActor,
  saveDraftActor,
  sessionLifetimeActor,
  tickActor,
  validateActor,
  writeActor,
  type CompileInput,
  type GeometryInput,
  type ReplayInput,
  type SaveDraftInput,
  type DeleteDraftInput,
  type TickInput,
  type ValidateInput,
  type WriteInput,
} from "./studio/actors";
import {
  DOCUMENT_MUTATING_EVENTS,
  type DocumentState,
  type PreviewScriptStep,
  type PreviewState,
  type StudioContext,
  type StudioEvent,
  type StudioMachineInput,
} from "./studio/types";
import { defaultLocalDraftStore, type StudioDraft } from "../document/localDrafts";

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
    sourceRevision: -1,
  };
}

function pushDiagnostic(document: DocumentState, message: string, path = ""): DocumentState {
  return { ...document, diagnostics: [...document.diagnostics, { path, message }] };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function errorPath(error: unknown): string {
  return (error && typeof error === "object" && "path" in error && typeof (error as { path: unknown }).path === "string")
    ? (error as { path: string }).path
    : "";
}

function slugifyDraftId(name: string): string {
  const slug = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
  return (slug.length > 0 && /^[a-z0-9]/.test(slug)) ? slug : `draft-${slug.length > 0 ? slug : "untitled"}`;
}

export const studioMachine = setup({
  types: {
    context: {} as StudioContext,
    events: {} as StudioEvent,
    input: {} as StudioMachineInput,
  },
  actors: {
    bootActor,
    editActor,
    validateActor,
    geometryActor,
    compileActor,
    writeActor,
    tickActor,
    replayActor,
    saveDraftActor,
    deleteDraftActor,
    sessionLifetimeActor,
  },
  guards: {
    engineReady: ({ context }) => (context.engine !== null && context.official !== null),
    documentCompiles: ({ context }) => (context.document.validation === "clean" && context.engine !== null),
  },
  actions: {
    applyLocalEdit: assign({
      selection: ({ context, event }) => ["OPEN_TEXT", "LOAD_DRAFT"].includes(event.type) ? { topology: null, ordinals: [], hovered: null } : context.selection,
      geometry: ({ context, event }) => ["OPEN_TEXT", "LOAD_DRAFT"].includes(event.type) ? {} : context.geometry,
      document: ({ context, event }) => {
        try {
          switch (event.type) {
            case "OPEN_TEXT": return openTextDocument(event.text, event.name);
            case "APPLY_TEXT": return applyText(context.document, event.text);
            case "EDIT_DOCUMENT": return editDocument(context.document, event.path, event.value, event.label);
            case "PAINT_CELLS": return paintCells(context.document, event.topology, event.row, event.ordinals, event.value);
            case "UNDO": return undoDocument(context.document);
            case "REDO": return redoDocument(context.document);
            case "LOAD_DRAFT": {
              const draft = context.machineInput.draftStore.load(event.id);
              if (!draft?.revisions.length) throw new Error(`no local draft named '${event.id}'.`);
              return openDraftDocument(draft.revisions[0].text, draft.documentName || event.id);
            }
            default: return context.document;
          }
        } catch (error) {
          return pushDiagnostic(context.document, errorMessage(error), errorPath(error));
        }
      },
    }),
    diagnoseEngineUnavailable: assign({
      document: ({ context }) =>
        pushDiagnostic(context.document, `engine unavailable: ${context.boot.refusal ?? "not booted yet."}`),
    }),
    diagnoseEditFailure: assign({
      document: ({ context, event }) => {
        const error = (event as unknown as { error: unknown }).error;
        return pushDiagnostic(context.document, errorMessage(error), errorPath(error));
      },
    }),
    diagnoseDraftFailure: assign({
      document: ({ context, event }) =>
        pushDiagnostic(context.document, errorMessage((event as unknown as { error: unknown }).error), "$draft"),
    }),
    releasePreviewHandle: ({ context }) => {
      if (context.engine && context.preview.handle) {
        void releasePreview(context.engine, context.preview.handle);
      }
    },
    invalidatePreview: assign({
      preview: () => initialPreview(),
    }),
  },
}).createMachine({
  id: "studio",
  context: ({ input }) => ({
    official: null,
    engine: null,
    boot: { status: "booting", refusal: null, build: null },
    document: createEmptyDocument(),
    geometry: {},
    selection: { topology: null, ordinals: [], hovered: null },
    preview: initialPreview(),
    machineInput: { ...input, draftStore: input.draftStore ?? defaultLocalDraftStore },
  }),
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
            engine: ({ event }) => event.output.engine,
            boot: ({ event }) => ({
              status: "ready" as const,
              refusal: null,
              build: {
                commit: event.output.version.commit,
                schemaVersion: event.output.version.schemaVersion,
                source: event.output.officialLoad.source,
              },
            }),
          }),
        },
        onError: {
          target: "ready",
          actions: assign({
            boot: ({ event }) => ({ status: "refused" as const, refusal: errorMessage((event as unknown as { error: unknown }).error), build: null }),
          }),
        },
      },
    },
    ready: {
      type: "parallel",
      invoke: { src: "sessionLifetimeActor", input: ({ context, self }) => ({ engine: context.engine, handle: () => self.getSnapshot().context.preview.handle }) },
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
      },
      states: {
        document: {
          initial: "idle",
          on: {
            ...Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].filter(type => type !== "OPEN_OFFICIAL").map(type => [type, {
              target: ".settling", actions: "applyLocalEdit",
            }])),
            OPEN_OFFICIAL: { target: ".editing" },
            SET_TEXT_DRAFT: {
              actions: assign({ document: ({ context, event }) => ({ ...context.document, text: event.text }) }),
            },
          },
          states: {
            idle: {
              on: {
                SAVE_DRAFT: { target: "savingDraft" },
                DELETE_DRAFT: { target: "deletingDraft" },
              },
            },
            settling: {
              on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].filter(type => type !== "OPEN_OFFICIAL").map(type => [type, {
                target: "settling", reenter: true, actions: "applyLocalEdit",
              }])),
              after: { 250: "checking" },
            },
            checking: {
              always: [
                { guard: ({ context }) => context.document.validation !== "pending", target: "idle" },
                { guard: "engineReady", target: "validating" },
                { target: "idle", actions: "diagnoseEngineUnavailable" },
              ],
            },
            editing: {
              on: { OPEN_OFFICIAL: { target: "editing", reenter: true } },
              invoke: {
                src: "editActor",
                input: ({ context, event }) => ({
                  perform: (): DocumentState | Promise<DocumentState> => {
                    if (event.type !== "OPEN_OFFICIAL" || !context.official) {
                      throw new Error("cannot open an official document before boot completes.");
                    }
                    const entry = context.official.manifest.documents.find(candidate => candidate.name === event.name);
                    if (!entry) throw new Error(`official manifest names no document '${event.name}'.`);
                    return context.official.documents.get(event.name)
                      .then(text => openOfficialDocument(event.name, text, entry.role));
                  },
                }),
                onDone: [
                  {
                    guard: ({ context, event }) => event.output === context.document,
                    target: "idle",
                  },
                  {
                    guard: "engineReady",
                    actions: assign({ document: ({ event }) => event.output, selection: () => ({ topology: null, ordinals: [], hovered: null }), geometry: () => ({}) }),
                    target: "validating",
                  },
                  {
                    actions: [
                      assign({ document: ({ event }) => event.output }),
                      "diagnoseEngineUnavailable",
                    ],
                    target: "idle",
                  },
                ],
                onError: {
                  actions: "diagnoseEditFailure",
                  target: "idle",
                },
              },
            },
            validating: {
              invoke: {
                src: "validateActor",
                input: ({ context }): ValidateInput => ({
                  engine: context.engine!,
                  official: context.official!,
                  document: {
                    name: context.document.name,
                    role: context.document.role,
                    text: context.document.appliedText,
                    value: context.document.value,
                  },
                }),
                onDone: [
                  {
                    guard: ({ event }) => event.output.ok,
                    actions: assign({
                      document: ({ context, event }) => ({
                        ...context.document,
                        diagnostics: event.output.diagnostics,
                        deferred: event.output.deferred,
                        composed: event.output.composed,
                        validation: "clean" as const,
                      }),
                    }),
                    target: "geometrizing",
                  },
                  {
                    actions: assign({
                      document: ({ context, event }) => ({
                        ...context.document,
                        diagnostics: event.output.diagnostics,
                        deferred: event.output.deferred,
                        composed: event.output.composed,
                        validation: "refused" as const,
                      }),
                      geometry: () => ({}),
                    }),
                    target: "idle",
                  },
                ],
                onError: {
                  actions: assign({
                    document: ({ context, event }) => ({
                      ...pushDiagnostic(context.document, errorMessage((event as unknown as { error: unknown }).error)),
                      validation: "refused" as const,
                    }),
                  }),
                  target: "idle",
                },
              },
            },
            geometrizing: {
              invoke: {
                src: "geometryActor",
                input: ({ context }): GeometryInput => ({ engine: context.engine!, value: context.document.value }),
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
                    document: ({ context, event }) =>
                      (event.output.diagnostics.length === 0
                        ? context.document
                        : { ...context.document, diagnostics: [...context.document.diagnostics, ...event.output.diagnostics] }),
                  }),
                },
                onError: {
                  target: "idle",
                  actions: assign({
                    document: ({ context, event }) =>
                      pushDiagnostic(context.document, errorMessage((event as unknown as { error: unknown }).error)),
                  }),
                },
              },
            },
            savingDraft: {
              invoke: {
                src: "saveDraftActor",
                input: ({ context, event }): SaveDraftInput => {
                  if (event.type !== "SAVE_DRAFT") {
                    throw new Error("savingDraft entered without SAVE_DRAFT.");
                  }
                  const id = event.id ?? slugifyDraftId(context.document.name);
                  const title = event.title ?? id;
                  return {
                    text: context.document.text,
                    perform: (): StudioDraft =>
                      context.machineInput.draftStore.save(id, title, context.document.name, context.document.text, context.document.label),
                  };
                },
                onDone: {
                  target: "idle",
                  actions: assign({
                    document: ({ context, event }) =>
                      ({ ...context.document, savedText: event.output.text }),
                  }),
                },
                onError: { target: "idle", actions: "diagnoseDraftFailure" },
              },
            },
            deletingDraft: {
              invoke: {
                src: "deleteDraftActor",
                input: ({ context, event }): DeleteDraftInput => {
                  if (event.type !== "DELETE_DRAFT") {
                    throw new Error("deletingDraft entered without DELETE_DRAFT.");
                  }
                  return { perform: (): boolean => context.machineInput.draftStore.delete(event.id) };
                },
                onDone: { target: "idle" },
                onError: { target: "idle", actions: "diagnoseDraftFailure" },
              },
            },
          },
        },
        preview: {
          initial: "idle",
          // Applies in every child state below that doesn't declare its own handler for these
          // types (XState bubbles an unhandled event up to the nearest ancestor that does) — the
          // one place "a new document revision stops the preview" is implemented.
          on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS, "PREVIEW_STOP"].map((type) => [
            type,
            { target: ".idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
          ])),
          states: {
            idle: {
              on: {
                PREVIEW_START: [
                  { guard: "documentCompiles", target: "compiling" },
                  {
                    target: "refused",
                    actions: assign({
                      preview: () => ({
                        ...initialPreview(),
                        status: "refused" as const,
                        refusal: "the document has unresolved diagnostics; fix them before starting a preview.",
                      }),
                    }),
                  },
                ],
              },
            },
            refused: {
              on: {
                PREVIEW_START: [
                  { guard: "documentCompiles", target: "compiling" },
                ],
              },
            },
            compiling: {
              invoke: {
                src: "compileActor",
                input: ({ context }): CompileInput => ({ engine: context.engine!, sourceJson: context.document.composed ?? context.document.appliedText }),
                onDone: [
                  {
                    guard: ({ event }) => event.output.status === "ready",
                    target: "ready",
                    actions: assign({
                      preview: ({ context, event }) => ({
                        ...initialPreview(),
                        status: "ready" as const,
                        handle: event.output.handle,
                        rows: event.output.rows,
                        tick: 0n,
                        snapshots: event.output.snapshot ? [event.output.snapshot] : [],
                        sourceRevision: context.document.revision,
                      }),
                    }),
                  },
                  {
                    target: "refused",
                    actions: assign({
                      preview: ({ event }) => ({ ...initialPreview(), status: "refused" as const, refusal: event.output.refusal }),
                    }),
                  },
                ],
                onError: {
                  target: "refused",
                  actions: assign({
                    preview: ({ event }) => ({
                      ...initialPreview(),
                      status: "refused" as const,
                      refusal: errorMessage((event as unknown as { error: unknown }).error),
                    }),
                  }),
                },
              },
            },
            ready: {
              on: {
                PREVIEW_WRITE: { target: "writing" },
                PREVIEW_TICK: { target: "ticking" },
                PREVIEW_UNDO: { target: "restoring" },
                PREVIEW_REDO: { target: "restoring" },
                JUMP_TO_TICK: { target: "restoring" },
                RESET_WORLD: { target: "resetting" },
                PREVIEW_STOP: { target: "idle", actions: ["releasePreviewHandle", "invalidatePreview"] },
              },
            },
            writing: {
              invoke: {
                src: "writeActor",
                input: ({ context, event }): WriteInput => {
                  if (event.type !== "PREVIEW_WRITE") {
                    throw new Error("writing entered without PREVIEW_WRITE.");
                  }
                  return { engine: context.engine!, handle: context.preview.handle!, row: event.row, key: event.key, value: event.value, write: event.write };
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
                  actions: assign({
                    preview: ({ context, event }) => ({
                      ...context.preview,
                      refusals: [...context.preview.refusals, errorMessage((event as unknown as { error: unknown }).error)],
                    }),
                  }),
                },
              },
            },
            ticking: {
              invoke: {
                src: "tickActor",
                input: ({ context }): TickInput => ({
                  engine: context.engine!,
                  handle: context.preview.handle!,
                  nextTick: context.preview.tick + 1n,
                  scriptLength: branchPreview(context.preview).script.length,
                }),
                onDone: {
                  target: "ready",
                  actions: assign({
                    preview: ({ context, event }) => {
                      if (!event.output.ok) {
                        return { ...context.preview, refusals: [...context.preview.refusals, event.output.error] };
                      }
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
                  actions: assign({
                    preview: ({ context, event }) => ({
                      ...context.preview,
                      refusals: [...context.preview.refusals, errorMessage((event as unknown as { error: unknown }).error)],
                    }),
                  }),
                },
              },
            },
            restoring: {
              invoke: {
                src: "replayActor",
                input: ({ context, event }): ReplayInput => {
                  const requested = (event.type === "PREVIEW_UNDO") ? context.preview.cursor - 1
                    : (event.type === "PREVIEW_REDO") ? context.preview.cursor + 1
                      : (event as { index: number }).index;
                  const index = Math.max(0, Math.min(context.preview.snapshots.length - 1, requested));
                  return {
                    engine: context.engine!,
                    sourceJson: context.document.composed ?? context.document.appliedText,
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
                    actions: assign({
                      preview: ({ context, event }) => ({ ...context.preview, refusals: [...context.preview.refusals, event.output.refusal ?? "Preview restore refused."] }),
                    }),
                  },
                ],
                onError: {
                  target: "ready",
                  actions: assign({
                    preview: ({ context, event }) => ({
                      ...context.preview,
                      refusals: [...context.preview.refusals, errorMessage((event as unknown as { error: unknown }).error)],
                    }),
                  }),
                },
              },
            },
            resetting: {
              entry: ["releasePreviewHandle", "invalidatePreview"],
              invoke: {
                src: "compileActor",
                input: ({ context }): CompileInput => ({ engine: context.engine!, sourceJson: context.document.composed ?? context.document.appliedText }),
                onDone: [
                  {
                    guard: ({ event }) => event.output.status === "ready",
                    target: "ready",
                    actions: assign({
                      preview: ({ context, event }) => ({
                        ...initialPreview(),
                        status: "ready" as const,
                        handle: event.output.handle,
                        rows: event.output.rows,
                        snapshots: event.output.snapshot ? [event.output.snapshot] : [],
                        sourceRevision: context.document.revision,
                      }),
                    }),
                  },
                  {
                    target: "refused",
                    actions: assign({ preview: ({ event }) => ({ ...initialPreview(), status: "refused" as const, refusal: event.output.refusal }) }),
                  },
                ],
                onError: {
                  target: "refused",
                  actions: assign({
                    preview: ({ event }) => ({
                      ...initialPreview(),
                      status: "refused" as const,
                      refusal: errorMessage((event as unknown as { error: unknown }).error),
                    }),
                  }),
                },
              },
            },
          },
        },
      },
    },
  },
});
