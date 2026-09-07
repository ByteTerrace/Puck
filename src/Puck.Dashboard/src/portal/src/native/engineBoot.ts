// The official engine boot contract: boots Puck.World.Browser from a loaded official tree
// (`OfficialLoad`, see official/officialClient.ts's `loadOfficial`) rather than from a plain
// `main.mjs` URL the way `engineHost.createEngineHost` does. `workerBoot.ts` carries the actual
// fetch-verify-boot mechanics, shared with `engine.worker.ts`'s own worker-side handling; this
// file is the public entry point the machine package calls, plus the version/build check that
// applies identically to both hosting modes.
//
// `'worker'` mode's actual `Worker` construction lives in engineWorkerLauncher.ts, reached only
// through a dynamic `import()` inside `bootEngineFromOfficial`'s own `'worker'` branch below —
// never a static top-level import. That module reads `import.meta.url`, a genuine ES-module-only
// construct with no CommonJS equivalent; a static import here would pull it into THIS file's own
// module graph and make Node's loader treat this whole file as an ES module the moment anything
// requires it (verified: Node's module-format detection inspects a file's full source text, not
// just the paths actually executed), breaking every test in this repository that requires
// engineBoot.ts under `require.extensions['.ts']`'s CommonJS transpile. The dynamic import defers
// that cost to an actual `'worker'`-mode boot, which only ever happens in a real browser tab
// anyway (Node has no `Worker` global either).
import type { OfficialLoad } from "../official/officialClient";
import { OfficialRefusal } from "../official/verify";
import type { WorldEngine } from "./engineTypes";
import { bootEngineFromOfficialFiles, type FetchLike } from "./workerBoot";
import { createInlineWorldEngine, dynamicImport } from "./inlineHost";

export interface EngineBootOptions {
  mode: "inline" | "worker";
  /** Overrides the `fetch` every official object is downloaded through. Meaningful only in
   * `'inline'` mode: a function cannot cross a Web Worker's postMessage boundary, so `'worker'`
   * mode always fetches with the worker's own global `fetch` — see engineWorkerLauncher.ts's own
   * remarks. */
  fetchImpl?: FetchLike;
}

function refuseVersionMismatch(official: OfficialLoad, version: { schemaVersion: string; commit: string }): never {
  throw new OfficialRefusal(
    `official refusal: the booted engine reports schemaVersion '${version.schemaVersion}' commit ` +
      `'${version.commit}', but the official manifest's build names schemaVersion ` +
      `'${official.build.worldSchema}' commit '${official.build.commit}'.`,
  );
}

/**
 * Boots Puck.World.Browser from the official tree `official` names: every engine file is fetched
 * by its manifest object URL, hash-verified, and stored in the byte store before dotnet's own
 * runtime ever runs (see workerBoot.ts's `bootEngineFromOfficialFiles`). After boot, in both
 * modes, `engine.version()` must report `official.build`'s own `worldSchema`/`commit` exactly —
 * a mismatch disposes the engine and throws an `OfficialRefusal` naming both.
 */
export async function bootEngineFromOfficial(official: OfficialLoad, options: EngineBootOptions): Promise<WorldEngine> {
  const engine =
    options.mode === "worker"
      ? await (await import("./engineWorkerLauncher")).createWorkerEngine({ kind: "boot", engineFiles: official.engineFiles })
      : await bootEngineFromOfficialFiles({ engineFiles: official.engineFiles }, options.fetchImpl ?? fetch);

  const version = await engine.version();
  if (version.schemaVersion !== official.build.worldSchema || version.commit !== official.build.commit) {
    await engine.dispose();
    refuseVersionMismatch(official, version);
  }

  return engine;
}

/**
 * Node-only convenience for tests: boots a `WorldEngine` in `'inline'` mode directly over an
 * AppBundle directory on disk (e.g. `dotnet publish`'s own output) — no official manifest, no
 * hash verification, exactly `main.mjs`'s own default `createEngine()`. Reuses
 * `inlineHost.createInlineWorldEngine` unchanged: a real filesystem AppBundle has no
 * content-addressing problem, so `main.mjs`'s relative `import "./_framework/dotnet.js"` resolves
 * exactly as authored.
 */
export async function bootEngineFromLocalBundle(appBundleDir: string): Promise<WorldEngine> {
  const path: any = await dynamicImport("node:path");
  const { pathToFileURL }: any = await dynamicImport("node:url");

  const mainMjs = path.join(appBundleDir, "main.mjs");
  return createInlineWorldEngine({ mode: "inline", engineEntryUrl: pathToFileURL(mainMjs).href as string });
}
