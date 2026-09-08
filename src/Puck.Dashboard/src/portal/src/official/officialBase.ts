/**
 * Resolves the official content root: the tree that carries this build's shipped engine
 * AppBundle, generated world-schema bundle, and world documents, laid out as
 *
 *   <root>/<channel>/manifest.json     the only mutable object
 *   <root>/builds/<commit>/manifest.json
 *   <root>/objects/sha256/<hh>/<hex64>
 *
 * Every "path" recorded inside a fetched manifest is relative to <root> — resolve it with
 * `objectUrl`, never by hand-concatenating strings, so a root with or without a trailing slash
 * and a channel or object path with or without a leading slash all resolve the same way.
 *
 * Deliberately NOT reading `import.meta.env` in this module: a literal `import.meta` token
 * anywhere in a module this repository's Node/CommonJS test harness pulls into its require
 * graph (see tests/offline-preview.test.cjs's `require.extensions['.ts']` hook) makes Node's
 * loader conclude the whole file is an ES module and refuse to `require()` it — verified on
 * this repository's pinned Node build. Reading the two `VITE_PUCK_OFFICIAL_*` variables happens
 * at exactly one call site instead — `OfficialProvider.tsx`, a `.tsx` file the test harness never
 * requires — which passes them here as a plain object. That keeps this module a pure function of
 * its inputs and fully unit-testable.
 */

export interface OfficialEnv {
  readonly VITE_PUCK_OFFICIAL_BASE?: string;
  readonly VITE_PUCK_OFFICIAL_CHANNEL?: string;
}

export interface ResolvedOfficial {
  /** The official root, always resolved to an absolute URL ending in "/". */
  readonly root: URL;
  readonly channel: string;
  readonly manifestUrl: URL;
  /** Resolves a manifest-relative "path" (e.g. "objects/sha256/ab/abcd...") against the root. */
  objectUrl(path: string): URL;
}

export class OfficialConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "OfficialConfigError";
  }
}

// import.meta.env is undefined for a document fetched from disk; this default lets
// resolveOfficial's relative-URL math work in a Node test that never supplies one.
const DEFAULT_DOCUMENT_HREF = "http://puck.official.local/";

/**
 * @param env The two `VITE_PUCK_OFFICIAL_*` values, normally `import.meta.env` itself.
 * @param documentHref The href a relative `base` resolves against. Defaults to the current
 *   document location in a browser, and to a fixed placeholder origin under Node (tests pass an
 *   absolute `base` and never rely on this default).
 */
export function resolveOfficial(env: OfficialEnv, documentHref?: string): ResolvedOfficial {
  const base = env.VITE_PUCK_OFFICIAL_BASE;
  const channel = env.VITE_PUCK_OFFICIAL_CHANNEL;

  if (!base) {
    throw new OfficialConfigError("VITE_PUCK_OFFICIAL_BASE is not set.");
  }
  if (!channel) {
    throw new OfficialConfigError("VITE_PUCK_OFFICIAL_CHANNEL is not set.");
  }

  const origin =
    documentHref ?? (typeof location === "object" && location !== null ? location.href : DEFAULT_DOCUMENT_HREF);
  const root = new URL(base.endsWith("/") ? base : `${base}/`, origin);
  const manifestUrl = new URL(`${channel}/manifest.json`, root);

  return {
    root,
    channel,
    manifestUrl,
    objectUrl(path: string): URL {
      return new URL(path, root);
    },
  };
}
