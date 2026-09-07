import type { StudioContext } from "./types";

type Snapshot = { readonly context: StudioContext };

export const selectDocument = (snapshot: Snapshot) => snapshot.context.document;
export const selectPreview = (snapshot: Snapshot) => snapshot.context.preview;
export const selectBoot = (snapshot: Snapshot) => snapshot.context.boot;
export const selectSelection = (snapshot: Snapshot) => snapshot.context.selection;
export const selectGeometry = (snapshot: Snapshot) => snapshot.context.geometry;
export const selectOfficial = (snapshot: Snapshot) => snapshot.context.official;
export const selectEngine = (snapshot: Snapshot) => snapshot.context.engine;
export const selectCanUndo = (snapshot: Snapshot): boolean =>
  snapshot.context.document.past.length > 0 && snapshot.context.document.text === snapshot.context.document.appliedText;
export const selectCanRedo = (snapshot: Snapshot): boolean =>
  snapshot.context.document.future.length > 0 && snapshot.context.document.text === snapshot.context.document.appliedText;
export const selectIsDirty = (snapshot: Snapshot): boolean =>
  snapshot.context.document.text !== snapshot.context.document.savedText;
