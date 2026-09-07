/**
 * The preview region's engine calls: compiling a session, writing a register, ticking, and
 * replaying a recorded write/tick script against a freshly compiled handle (the engine has no
 * snapshot-restore of its own — see `engineTypes.ts`'s own `WorldEngine` surface). Every function
 * here is a thin, directly-testable wrapper around real `WorldEngine` calls; `studioMachine.ts`
 * invokes them as actors and folds the result into `context.preview`.
 */
import type { RowInfo, WorldEngine } from "../../native/engineTypes";
import type { PreviewScriptStep, PreviewSnapshot } from "./types";

export interface PreviewOutcome {
  readonly status: "ready" | "refused";
  readonly handle: string | null;
  readonly rows: readonly RowInfo[];
  readonly tick: bigint;
  readonly refusal: string | null;
}

function refused(refusal: string): PreviewOutcome {
  return { status: "refused", handle: null, rows: [], tick: 0n, refusal };
}

/** Compiles `sourceJson` into a fresh session and reads its initial rows/hash as tick-0 snapshot. */
export async function compilePreview(
  engine: WorldEngine,
  sourceJson: string,
): Promise<PreviewOutcome & { snapshot: PreviewSnapshot | null }> {
  const compiled = await engine.compile(sourceJson);
  if (!compiled.ok) {
    return { ...refused(compiled.errors.map((error) => `${error.path}: ${error.message}`).join("; ")), snapshot: null };
  }
  const rows = await engine.rows(compiled.handle);
  const hash = await engine.stateHash(compiled.handle);
  const snapshot: PreviewSnapshot = { tick: 0n, rows, hash, trace: null, scriptLength: 0 };
  return { status: "ready", handle: compiled.handle, rows, tick: 0n, refusal: null, snapshot };
}

export async function writePreviewRow(
  engine: WorldEngine,
  handle: string,
  row: string,
  key: string | undefined,
  value: bigint,
  write: "set" | "add",
): Promise<{ ok: true; rows: readonly RowInfo[] } | { ok: false; error: string }> {
  const result = await engine.writeRow(handle, row, key, value, write);
  if (!result.ok) {
    return { ok: false, error: result.error ?? `writeRow: '${row}' refused the write.` };
  }
  return { ok: true, rows: await engine.rows(handle) };
}

export async function tickPreview(
  engine: WorldEngine,
  handle: string,
  nextTick: bigint,
  scriptLength: number,
): Promise<{ ok: true; snapshot: PreviewSnapshot } | { ok: false; error: string }> {
  const judged = await engine.judge(handle, nextTick);
  if (!judged.ok) {
    return { ok: false, error: judged.error };
  }
  const rows = await engine.rows(handle);
  const hash = await engine.stateHash(handle);
  return { ok: true, snapshot: { tick: nextTick, rows, hash, trace: judged.trace, scriptLength } };
}

/** Releases `handle` if given — never throws; a handle that is already gone (or was never
 * compiled) is not an error the caller needs to see. */
export async function releasePreview(engine: WorldEngine, handle: string | null): Promise<void> {
  if (handle) {
    await engine.release(handle);
  }
}

/**
 * Recreates a session by recompiling `sourceJson` fresh and replaying `script.slice(0, upTo)` in
 * order — the mechanism every history move (`PREVIEW_UNDO`/`PREVIEW_REDO`/`JUMP_TO_TICK`) shares.
 * The caller already knows the target snapshot's own recorded `rows`/`hash`/`tick`; this function's
 * job is only to leave a LIVE handle behind that agrees with them, ready for the next write or tick.
 */
export async function replayPreview(
  engine: WorldEngine,
  sourceJson: string,
  script: readonly PreviewScriptStep[],
  upTo: number,
): Promise<PreviewOutcome> {
  const compiled = await engine.compile(sourceJson);
  if (!compiled.ok) {
    return refused(compiled.errors.map((error) => `${error.path}: ${error.message}`).join("; "));
  }

  let tick = 0n;
  for (const step of script.slice(0, upTo)) {
    if (step.kind === "write") {
      const result = await engine.writeRow(compiled.handle, step.row, step.key, step.value, step.write);
      if (!result.ok) {
        await engine.release(compiled.handle);
        return refused(result.error ?? `replay: write to '${step.row}' refused.`);
      }
    } else {
      const judged = await engine.judge(compiled.handle, step.tick);
      if (!judged.ok) {
        await engine.release(compiled.handle);
        return refused(judged.error);
      }
      tick = step.tick;
    }
  }

  const rows = await engine.rows(compiled.handle);
  return { status: "ready", handle: compiled.handle, rows, tick, refusal: null };
}
