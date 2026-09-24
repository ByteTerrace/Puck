import type { SnapshotFrom } from "xstate";
import type { studioMachine } from "../studioMachine";
import type { StudioContext } from "./types";
import { engineRefusal } from "../studioMachine";
import { workspaceDirty, workspaceProblems } from "./workspace";

type Snapshot = { readonly context: StudioContext };
type MachineSnapshot = SnapshotFrom<typeof studioMachine>;

export const selectWorkspace = (snapshot: Snapshot) => snapshot.context.workspace;
export const selectPreview = (snapshot: Snapshot) => snapshot.context.preview;
export const selectBoot = (snapshot: Snapshot) => snapshot.context.boot;
export const selectLanguage = (snapshot: Snapshot) => snapshot.context.language;
/** Why no preview can start whatever the source does (a refused build or engine), or `null`. */
export const selectEngineRefusal = (snapshot: Snapshot): string | null => engineRefusal(snapshot.context);
export const selectWorld = (snapshot: Snapshot) => snapshot.context.world;
export const selectSelection = (snapshot: Snapshot) => snapshot.context.selection;
export const selectGeometry = (snapshot: Snapshot) => snapshot.context.geometry;
export const selectOfficial = (snapshot: Snapshot) => snapshot.context.official;
export const selectWorldEngine = (snapshot: Snapshot) => snapshot.context.worldEngine;
export const selectLanguageChannel = (snapshot: Snapshot) => snapshot.context.languageChannel;
export const selectCompiled = (snapshot: Snapshot) => snapshot.context.compiled;
export const selectDrafts = (snapshot: Snapshot) => snapshot.context.drafts;
export const selectRefusals = (snapshot: Snapshot) => snapshot.context.refusals;
/** The newest clean composition's document, or `null` before the first. */
export const selectComposition = (snapshot: Snapshot): unknown => snapshot.context.workspace?.composition ?? null;
/** Whether any workspace file differs from its saved text. */
export const selectIsDirty = (snapshot: Snapshot): boolean => workspaceDirty(snapshot.context.workspace);

// Questions about what the studio will accept now are asked of the machine, so no component repeats a state name.

/** Whether diagnostics are pending for the current version of the open file. */
export const selectChecking = (snapshot: MachineSnapshot): boolean => snapshot.hasTag("checking");
/** Whether a draft save would be taken now. */
export const selectCanSaveDraft = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "SAVE_DRAFT" });
/** Whether a draft delete would be taken now (one library write at a time). */
export const selectCanDeleteDrafts = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "DELETE_DRAFT", id: "draft" });
/** Whether the running preview can step back to an earlier recorded snapshot now. */
export const selectCanStepBack = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "PREVIEW_UNDO" });
/** Whether the running preview can step forward to a later recorded snapshot now. */
export const selectCanStepForward = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "PREVIEW_REDO" });
/** Whether a preview start would be taken now (not while booting, nor after a refusal the source has not fixed). */
export const selectCanStartPreview = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "PREVIEW_START" });
/** Whether the running preview takes commands (tick, write, reset, jump) now. */
export const selectCanDrivePreview = (snapshot: MachineSnapshot): boolean => snapshot.can({ type: "PREVIEW_TICK" });
/** Whether the preview is compiling, writing, ticking, restoring or resetting. */
export const selectPreviewBusy = (snapshot: MachineSnapshot): boolean => snapshot.hasTag("preview-busy");

/** Every problem the workspace reports at its current revision: both diagnostic tiers and the composition's refusals. */
export const selectProblems = (snapshot: Snapshot) => (snapshot.context.workspace ? workspaceProblems(snapshot.context.workspace) : []);
