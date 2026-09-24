import { createHash } from "node:crypto";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import type { Plugin, ResolvedConfig } from "vite";

/**
 * The DuckDB extensions the portal serves itself, pinned by SHA-256 (`portal/duckdb-extensions.json`).
 *
 * DuckDB-Wasm's npm package carries only the core engine: Parquet, JSON, httpfs, and ICU are extensions it downloads at
 * run time, by default from DuckDB's own repository. The portal serves them from its own origin instead, so a query
 * never depends on a third party and the bytes it runs are the bytes this manifest names. DuckDB composes each URL
 * itself (`<repository>/<duckdb version>/<platform>/<name>.duckdb_extension.wasm`), so the files keep that layout rather
 * than hashed asset names; the version in the path is what makes them immutable.
 */
export interface DuckdbExtensionManifest {
  /** Where the pinned files are downloaded from at build time. */
  repository: string;
  /** The DuckDB version the engine reports (`SELECT version()`); extensions only load into the version they were built for. */
  version: string;
  /** `<platform>/<extension>` to the file's SHA-256, for every platform bundle the portal ships. */
  files: Record<string, string>;
}

/** The module the engine reads its extension repository's URL from. */
export const REPOSITORY_MODULE = "virtual:duckdb-extension-repository";

const RESOLVED_REPOSITORY_MODULE = `\0${REPOSITORY_MODULE}`;
// Where the files are served from in development, and where they are written in the build (under `assets/`' parent).
const DEV_PREFIX = "/@duckdb-extensions";
const BUILD_DIRECTORY = "duckdb-extensions";

const sha256 = (bytes: Uint8Array) => createHash("sha256").update(bytes).digest("hex");

/** The pinned file for `key`, from the cache when it is there and intact, otherwise downloaded and checked. */
async function pinnedFile(manifest: DuckdbExtensionManifest, cacheDirectory: string, key: string): Promise<Uint8Array> {
  const expected = manifest.files[key];
  const cached = path.join(cacheDirectory, manifest.version, `${key}.duckdb_extension.wasm`);
  const existing = await readFile(cached).catch(() => undefined);

  if (existing && sha256(existing) === expected) {
    return existing;
  }

  const url = `${manifest.repository}/${manifest.version}/${key}.duckdb_extension.wasm`;
  const response = await fetch(url);

  if (!response.ok) {
    throw new Error(`DuckDB extension ${key} could not be downloaded from ${url} (HTTP ${response.status}).`);
  }

  const bytes = new Uint8Array(await response.arrayBuffer());
  const actual = sha256(bytes);

  if (actual !== expected) {
    throw new Error(`DuckDB extension ${key} from ${url} has SHA-256 ${actual}; the manifest pins ${expected}.`);
  }

  await mkdir(path.dirname(cached), { recursive: true });
  await writeFile(cached, bytes);

  return bytes;
}

/** Serves the manifest's extensions from the portal's own origin, and tells the engine where they are. */
export function duckdbExtensions(manifestPath: string): Plugin {
  let config: ResolvedConfig;
  let manifest: DuckdbExtensionManifest;
  let files: Map<string, Uint8Array>;

  const load = async () => {
    manifest = JSON.parse(await readFile(manifestPath, "utf8")) as DuckdbExtensionManifest;

    const cacheDirectory = path.join(config.cacheDir, "duckdb-extensions");
    const keys = Object.keys(manifest.files);

    files = new Map(await Promise.all(keys.map(async (key) => [key, await pinnedFile(manifest, cacheDirectory, key)] as const)));
  };

  return {
    name: "puck-duckdb-extensions",
    configResolved(resolved) {
      config = resolved;
    },
    async buildStart() {
      await load();
    },
    resolveId(id) {
      return REPOSITORY_MODULE === id ? RESOLVED_REPOSITORY_MODULE : undefined;
    },
    load(id) {
      if (RESOLVED_REPOSITORY_MODULE !== id) {
        return undefined;
      }

      // The repository's absolute URL, resolved where the code runs: the dev server's origin in development, and the
      // build's root (the parent of `assets/`, where this module's chunk lands; checked in `generateBundle`) otherwise.
      // It names a directory of files, not an asset, so Vite is told not to resolve it at build time.
      const location = "serve" === config.command ? DEV_PREFIX : `../${BUILD_DIRECTORY}`;

      return `export default new URL(/* @vite-ignore */ ${JSON.stringify(location)}, import.meta.url).href;\n`;
    },
    configureServer(server) {
      server.middlewares.use(DEV_PREFIX, (request, response, next) => {
        const match = /^\/([^/]+)\/(.+)\.duckdb_extension\.wasm$/.exec(request.url ?? "");
        const bytes = match && match[1] === manifest?.version ? files?.get(match[2]) : undefined;

        if (!bytes) {
          next();
          return;
        }

        // The engine's worker runs on the host's origin in development, not the portal's.
        response.setHeader("Access-Control-Allow-Origin", "*");
        response.setHeader("Content-Type", "application/wasm");
        response.end(bytes);
      });
    },
    generateBundle(_options, bundle) {
      for (const output of Object.values(bundle)) {
        if ("chunk" === output.type && output.moduleIds.includes(RESOLVED_REPOSITORY_MODULE) && !output.fileName.startsWith(`${config.build.assetsDir}/`)) {
          this.error(`The DuckDB extension repository is resolved relative to ${config.build.assetsDir}/, but its chunk is ${output.fileName}.`);
        }
      }

      for (const [key, bytes] of files) {
        this.emitFile({ fileName: `${BUILD_DIRECTORY}/${manifest.version}/${key}.duckdb_extension.wasm`, source: bytes, type: "asset" });
      }
    },
  };
}
