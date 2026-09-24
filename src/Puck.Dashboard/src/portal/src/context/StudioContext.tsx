import { createActorContext } from "@xstate/react";
import { studioMachine } from "../machines/studioMachine";
import {
  selectBoot,
  selectLanguage,
  selectEngineRefusal,
  selectCanDeleteDrafts,
  selectCanDrivePreview,
  selectCanSaveDraft,
  selectCanStartPreview,
  selectCanStepBack,
  selectCanStepForward,
  selectChecking,
  selectCompiled,
  selectComposition,
  selectDrafts,
  selectGeometry,
  selectIsDirty,
  selectLanguageChannel,
  selectOfficial,
  selectPreview,
  selectPreviewBusy,
  selectProblems,
  selectRefusals,
  selectSelection,
  selectWorkspace,
  selectWorld,
  selectWorldEngine,
} from "../machines/studio/selectors";

/**
 * React seam for `studioMachine` — components read the studio's state through the hooks below instead of
 * prop-drilling a machine reference. Constructing the actor (via `<StudioContext.Provider options={{ input: {...} }}>`)
 * is WorldStudio.tsx's job: it is the one place that knows the real `bootEngine` seam (`bootEngineFromOfficial` from
 * `native/engineBoot.ts`) and the resolved official environment.
 */
export const StudioContext = createActorContext(studioMachine);

const sameItems = <T,>(left: readonly T[], right: readonly T[]) =>
  left.length === right.length && left.every((item, index) => item === right[index]);

export function useStudioBoot() {
  return StudioContext.useSelector(selectBoot);
}

export function useStudioEngineRefusal() {
  return StudioContext.useSelector(selectEngineRefusal);
}

export function useStudioLanguage() {
  return StudioContext.useSelector(selectLanguage);
}

export function useStudioWorld() {
  return StudioContext.useSelector(selectWorld);
}

export function useStudioWorkspace() {
  return StudioContext.useSelector(selectWorkspace);
}

export function useStudioComposition() {
  return StudioContext.useSelector(selectComposition);
}

export function useStudioCompiled() {
  return StudioContext.useSelector(selectCompiled);
}

export function useStudioProblems() {
  return StudioContext.useSelector(selectProblems, sameItems);
}

export function useStudioRefusals() {
  return StudioContext.useSelector(selectRefusals);
}

export function useStudioChecking() {
  return StudioContext.useSelector(selectChecking);
}

export function useStudioPreview() {
  return StudioContext.useSelector(selectPreview);
}

export function useStudioSelection() {
  return StudioContext.useSelector(selectSelection);
}

export function useStudioGeometry() {
  return StudioContext.useSelector(selectGeometry);
}

export function useStudioOfficial() {
  return StudioContext.useSelector(selectOfficial);
}

export function useStudioWorldEngine() {
  return StudioContext.useSelector(selectWorldEngine);
}

export function useStudioLanguageChannel() {
  return StudioContext.useSelector(selectLanguageChannel);
}

export function useStudioIsDirty() {
  return StudioContext.useSelector(selectIsDirty);
}

export function useStudioDrafts() {
  return StudioContext.useSelector(selectDrafts);
}

export function useStudioCanSaveDraft() {
  return StudioContext.useSelector(selectCanSaveDraft);
}

export function useStudioCanDeleteDrafts() {
  return StudioContext.useSelector(selectCanDeleteDrafts);
}

export function useStudioCanStartPreview() {
  return StudioContext.useSelector(selectCanStartPreview);
}

export function useStudioCanDrivePreview() {
  return StudioContext.useSelector(selectCanDrivePreview);
}

export function useStudioCanStepBack() {
  return StudioContext.useSelector(selectCanStepBack);
}

export function useStudioCanStepForward() {
  return StudioContext.useSelector(selectCanStepForward);
}

export function useStudioPreviewBusy() {
  return StudioContext.useSelector(selectPreviewBusy);
}
