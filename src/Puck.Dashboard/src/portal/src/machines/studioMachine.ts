/**
 * The studio statechart: owns the official content load, the live engine session, the authored
 * document (with undo/redo and validation), per-topology geometry, cell selection, and the
 * preview session. Built beside `machines/worldSimulationMachine.ts` (the old, JS-only preview
 * machine) — phase 3 deletes that machine and its components once they move over to this one; this
 * machine never touches React (see `context/StudioContext.tsx` for the React seam) or `native/`,
 * `forms/`, or `official/` directly beyond the read-only calls their own public contracts offer.
 *
 * State shape: `booting` (invoke `loadOfficial` then the boot seam — see `studio/types.ts`'s own
 * `BootEngine` remarks) always resolves to `ready`, refused or not — "editing continues in
 * refused; every engine-backed action answers with a diagnostic naming the refusal" (this
 * package's own contract). `ready` is a parallel state: the `document` region owns text/value/
 * undo/redo/validation/geometry, the `preview` region owns the compiled session and its tick
 * history. A document-mutating event (see `DOCUMENT_MUTATING_EVENTS`) is visible to BOTH regions
 * in the same microstep: the document region applies the edit, and the preview region — wherever
 * it is — releases its handle and falls back to idle, so a preview can never run silently against
 * a document the engine has not (re)validated.
 *
 * `PREVIEW_START`'s guard reads `document.validation === 'clean'` — a status distinct from
 * `diagnostics.length === 0`, which is ALSO true before validation has ever run (a fresh or
 * just-edited document starts with no diagnostics of its own). `validation` starts, and stays,
 * `'pending'` until a validation actually completes against the live engine (see `studio/types.ts`'s
 * own `DocumentValidationStatus` remarks), so a caller that fires `PREVIEW_START` while a validation is
 * in flight — or before one has ever run — is correctly refused rather than starting a preview the
 * engine has not actually checked yet.
 */
import { assign, setup } from "xstate";
import { readDocumentRole } from "../document/documentRole";
import { parseDocumentText } from "../document/jsonText";
import {
  applyText,
  createEmptyDocument,
  editDocument,
  openOfficialDocument,
  openTextDocument,
  paintCells,
  redoDocument,
  undoDocument,
} from "./studio/document";
import { releasePreview } from "./studio/preview";
import {
  bootActor,
  compileActor,
  deleteDraftActor,
  editActor,
  geometryActor,
  replayActor,
  saveDraftActor,
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
  },
  guards: {
    engineReady: ({ context }) => (context.engine !== null && context.official !== null),
    documentCompiles: ({ context }) => (context.document.validation === "clean" && context.engine !== null),
  },
  actions: {
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
      on: {
        SELECT_TOPOLOGY: {
          actions: assign({ selection: ({ context, event }) => ({ ...context.selection, topology: event.name }) }),
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
          states: {
            idle: {
              on: {
                OPEN_OFFICIAL: { target: "editing" },
                OPEN_TEXT: { target: "editing" },
                APPLY_TEXT: { target: "editing" },
                EDIT_DOCUMENT: { target: "editing" },
                PAINT_CELLS: { target: "editing" },
                UNDO: { target: "editing" },
                REDO: { target: "editing" },
                LOAD_DRAFT: { target: "editing" },
                SET_TEXT_DRAFT: {
                  actions: assign({ document: ({ context, event }) => ({ ...context.document, text: event.text }) }),
                },
                SAVE_DRAFT: { target: "savingDraft" },
                DELETE_DRAFT: { target: "deletingDraft" },
              },
            },
            // Re-entrant on the very same event set: a new document-mutating event while an edit
            // is in flight interrupts it (XState stops an exited state's invoked actor on exit —
            // but ONLY on a transition that actually exits: a same-target self-transition like this
            // one is INTERNAL by default and leaves the current invoke running untouched unless
            // `reenter: true` says otherwise — see xstate's own `getTransitionDomain`). `reenter:
            // true` is what makes this transition really EXIT "editing" (stopping the in-flight
            // editActor) before re-entering it fresh for the new event, so only the NEWEST edit's
            // editActor ever resolves into `context.document`.
            editing: {
              on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].map((type) => [type, { target: "editing", reenter: true }])),
              invoke: {
                src: "editActor",
                input: ({ context, event }) => ({
                  perform: (): DocumentState | Promise<DocumentState> => {
                    switch (event.type) {
                      case "OPEN_OFFICIAL": {
                        if (!context.official) {
                          throw new Error("cannot open an official document before boot completes.");
                        }
                        const entry = context.official.manifest.documents.find((candidate) => candidate.name === event.name);
                        if (!entry) {
                          throw new Error(`official manifest names no document '${event.name}'.`);
                        }
                        return context.official.documents.get(event.name)
                          .then((text) => openOfficialDocument(event.name, text, entry.role));
                      }
                      case "OPEN_TEXT":
                        return openTextDocument(event.text, event.name);
                      case "APPLY_TEXT":
                        return applyText(context.document, event.text);
                      case "EDIT_DOCUMENT":
                        return editDocument(context.document, event.path, event.value, event.label);
                      case "PAINT_CELLS":
                        return paintCells(context.document, event.topology, event.row, event.ordinals, event.value);
                      case "UNDO":
                        return undoDocument(context.document);
                      case "REDO":
                        return redoDocument(context.document);
                      case "LOAD_DRAFT": {
                        const draft = context.machineInput.draftStore.load(event.id);
                        if (!draft || draft.revisions.length === 0) {
                          throw new Error(`no local draft named '${event.id}'.`);
                        }
                        // A draft's own text was, at save time, already-verified official content
                        // or content that already passed `checkDocument` once (OPEN_TEXT/APPLY_TEXT)
                        // — this is `openOfficialDocument`'s trusted-content path, not
                        // `openTextDocument`'s pasted-content one, so `checkDocument` never re-runs
                        // here. `parseDocumentText` (not plain `JSON.parse`) still parses it once to
                        // pick a role — the same Int64-faithful parse `openOfficialDocument` itself
                        // performs a moment later, so an out-of-range sentinel reads back as the
                        // same `bigint` both times rather than a rounded double on this first pass.
                        const text = draft.revisions[0].text;
                        const value: unknown = parseDocumentText(text);
                        return openOfficialDocument(draft.documentName || event.id, text, readDocumentRole(value));
                      }
                      default:
                        throw new Error(`document.editing: unexpected event '${(event as { type: string }).type}'.`);
                    }
                  },
                }),
                onDone: [
                  {
                    guard: ({ context, event }) => event.output === context.document,
                    target: "idle",
                  },
                  {
                    guard: "engineReady",
                    actions: assign({ document: ({ event }) => event.output }),
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
            // Unlike "editing"'s own self-transition (see its remarks on `reenter`), this one's
            // target ("editing") differs from its source ("validating") — a genuine cross-state
            // transition always exits its source, so the in-flight validateActor (the expensive
            // `composeTree`/`compile` call) is always stopped here with no `reenter` flag needed;
            // its own eventual result, even if it later resolves anyway, is never applied.
            validating: {
              on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].map((type) => [type, { target: "editing" }])),
              invoke: {
                src: "validateActor",
                input: ({ context }): ValidateInput => ({
                  engine: context.engine!,
                  official: context.official!,
                  document: {
                    name: context.document.name,
                    role: context.document.role,
                    text: context.document.text,
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
              on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].map((type) => [type, { target: "editing" }])),
              invoke: {
                src: "geometryActor",
                input: ({ context }): GeometryInput => ({ engine: context.engine!, value: context.document.value }),
                onDone: {
                  target: "idle",
                  actions: assign({
                    geometry: ({ event }) => event.output.geometry,
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
                    revision: context.document.revision,
                    perform: (): StudioDraft =>
                      context.machineInput.draftStore.save(id, title, context.document.name, context.document.text, context.document.label),
                  };
                },
                onDone: {
                  target: "idle",
                  actions: assign({
                    document: ({ context, event }) =>
                      (event.output.revision === context.document.revision
                        ? { ...context.document, cleanRevision: context.document.revision }
                        : context.document),
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
          on: Object.fromEntries([...DOCUMENT_MUTATING_EVENTS].map((type) => [
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
                input: ({ context }): CompileInput => ({ engine: context.engine!, sourceJson: context.document.composed ?? context.document.text }),
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
                    preview: ({ context, event }) => (event.output.ok
                      ? { ...context.preview, rows: event.output.rows, script: [...context.preview.script, event.output.step] }
                      : { ...context.preview, refusals: [...context.preview.refusals, event.output.error] }),
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
                  scriptLength: context.preview.script.length,
                }),
                onDone: {
                  target: "ready",
                  actions: assign({
                    preview: ({ context, event }) => {
                      if (!event.output.ok) {
                        return { ...context.preview, refusals: [...context.preview.refusals, event.output.error] };
                      }
                      const snapshot = event.output.snapshot;
                      const script: PreviewScriptStep[] = [...context.preview.script, { kind: "tick", tick: snapshot.tick }];
                      return {
                        ...context.preview,
                        tick: snapshot.tick,
                        rows: snapshot.rows,
                        snapshots: [...context.preview.snapshots, snapshot],
                        cursor: context.preview.snapshots.length,
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
                    sourceJson: context.document.composed ?? context.document.text,
                    script: context.preview.script,
                    upTo: context.preview.snapshots[index]?.scriptLength ?? 0,
                    index,
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
                    target: "refused",
                    actions: assign({
                      preview: ({ context, event }) => ({ ...context.preview, status: "refused" as const, handle: null, refusal: event.output.refusal }),
                    }),
                  },
                ],
                onError: {
                  target: "refused",
                  actions: assign({
                    preview: ({ context, event }) => ({
                      ...context.preview,
                      status: "refused" as const,
                      handle: null,
                      refusal: errorMessage((event as unknown as { error: unknown }).error),
                    }),
                  }),
                },
              },
            },
            resetting: {
              entry: "releasePreviewHandle",
              invoke: {
                src: "compileActor",
                input: ({ context }): CompileInput => ({ engine: context.engine!, sourceJson: context.document.composed ?? context.document.text }),
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
