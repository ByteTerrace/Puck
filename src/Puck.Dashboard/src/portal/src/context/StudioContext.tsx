import { createActorContext } from "@xstate/react";
import { studioMachine } from "../machines/studioMachine";
import {
  selectBoot,
  selectCanRedo,
  selectCanUndo,
  selectDocument,
  selectEngine,
  selectGeometry,
  selectIsDirty,
  selectOfficial,
  selectPreview,
  selectSelection,
} from "../machines/studio/selectors";

/**
 * React seam for `studioMachine` — phase 3's components read the studio's state through the
 * hooks below instead of prop-drilling a machine reference. Constructing the actor (via
 * `<StudioContext.Provider options={{ input: {...} }}>`) is phase 3's job: it is the one place
 * that knows the real `bootEngine` seam (`bootEngineFromOfficial` from `native/engineBoot.ts`)
 * and the resolved official environment — see `studioMachine.ts`'s own remarks on why the machine
 * itself never imports either.
 */
export const StudioContext = createActorContext(studioMachine);

export function useStudioBoot() {
  return StudioContext.useSelector(selectBoot);
}

export function useStudioDocument() {
  return StudioContext.useSelector(selectDocument);
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

export function useStudioEngine() {
  return StudioContext.useSelector(selectEngine);
}

export function useStudioCanUndo() {
  return StudioContext.useSelector(selectCanUndo);
}

export function useStudioCanRedo() {
  return StudioContext.useSelector(selectCanRedo);
}

export function useStudioIsDirty() {
  return StudioContext.useSelector(selectIsDirty);
}
