import type { TokenCredential } from "@azure/identity";
import type { AsyncDuckDB, DuckDBBundles } from "@duckdb/duckdb-wasm";
import ehModule from "@duckdb/duckdb-wasm/dist/duckdb-eh.wasm?url";
import mvpModule from "@duckdb/duckdb-wasm/dist/duckdb-mvp.wasm?url";
import ehWorker from "@duckdb/duckdb-wasm/dist/duckdb-browser-eh.worker.js?url";
import mvpWorker from "@duckdb/duckdb-wasm/dist/duckdb-browser-mvp.worker.js?url";
import extensionRepository from "virtual:duckdb-extension-repository";
import { collectRows, type QueryOutput, RESULT_ROW_LIMIT } from "./queryResult";
import { sqlStringLiteral, STORAGE_TOKEN_SCOPES } from "./storage";
import { storageOriginsIn, storageSecretsSql } from "./storageSecrets";

export type { QueryOutput } from "./queryResult";

// DuckDB ships with the portal, pinned by the lockfile, rather than loading whatever a CDN tag points at. The package is
// a Puck build of DuckDB-Wasm (see the dashboard README): upstream's HTTP client ignores an HTTP secret's BEARER_TOKEN.
const bundles: DuckDBBundles = {
  eh: { mainModule: ehModule, mainWorker: ehWorker },
  mvp: { mainModule: mvpModule, mainWorker: mvpWorker },
};

/**
 * Run once on every new engine, before any query. Every setting is GLOBAL: a plain SET lasts only as long as the
 * connection that ran it, and each query runs on a connection of its own.
 *
 * - Extensions (Parquet, JSON, httpfs, ICU; the npm package carries only the core engine) load from the portal's own
 *   origin, pinned by `duckdb-extensions.json`, never from DuckDB's repository by default. One the portal does not
 *   serve fails to load; community extensions are refused. SQL that names a repository itself (`INSTALL spatial FROM
 *   core`) still reaches it, as the user asked.
 * - Remote files are read by the httpfs extension. Unlike the built-in reader, which reports every failed request as
 *   "No files found", it names the HTTP status that failed.
 * - ICU loads up front: a cast that needs it (`TIMESTAMPTZ` to `DATE`) is bound before autoloading would load it, so
 *   the first such query would fail.
 * - The session is in UTC, the zone the results table prints every `TIMESTAMPTZ` in, so SQL that formats a time
 *   itself agrees with the table.
 */
export const engineSetup = (extensionRepository: string) =>
  [
    "SET GLOBAL builtin_httpfs = false",
    `SET GLOBAL custom_extension_repository = ${sqlStringLiteral(extensionRepository)}`,
    "SET GLOBAL allow_community_extensions = false",
    "LOAD httpfs",
    "LOAD icu",
    "SET GLOBAL TimeZone = 'UTC'",
  ].join(";\n");

let duckDbPromise: Promise<AsyncDuckDB> | undefined;

const getDuckDb = () =>
  (duckDbPromise ??= (async () => {
    const duckdb = await import("@duckdb/duckdb-wasm");
    const bundle = await duckdb.selectBundle(bundles);
    // The portal may be federated into a host on another origin, where a Worker cannot be constructed from the
    // portal's URL directly; a same-origin blob that imports the worker script works from either.
    const workerUrl = URL.createObjectURL(
      new Blob([`importScripts(${JSON.stringify(new URL(bundle.mainWorker!, import.meta.url).href)});`], {
        type: "text/javascript",
      }),
    );
    const db = new duckdb.AsyncDuckDB(new duckdb.VoidLogger(), new Worker(workerUrl));

    await db.instantiate(new URL(bundle.mainModule, import.meta.url).href, bundle.pthreadWorker);

    const setup = await db.connect();

    try {
      await setup.query(engineSetup(extensionRepository));
    } finally {
      await setup.close();
    }

    return db;
  })());

/**
 * The secrets that authorize the query's storage reads, or nothing when it names no ByteTerrace storage or no one is
 * signed in (the engine then reads without a token, which a public file allows).
 */
async function storageSecretsFor(sqlText: string, tokenCredential: TokenCredential): Promise<string> {
  const origins = storageOriginsIn(sqlText);

  if (0 === origins.length) {
    return "";
  }

  const accessToken = await tokenCredential.getToken(STORAGE_TOKEN_SCOPES);

  return accessToken ? storageSecretsSql(origins, accessToken.token) : "";
}

/**
 * Runs SQL in the browser's own DuckDB, reading the user's storage files by URL with their token, and answers with at
 * most `rowLimit` rows: the result streams from the engine and stops being read once the limit is reached. Aborting
 * `signal` cancels the query in the engine, not just its answer. Throws with DuckDB's own reason.
 */
export async function runStorageQuery(
  sqlText: string,
  tokenCredential: TokenCredential,
  signal: AbortSignal,
  rowLimit: number = RESULT_ROW_LIMIT,
): Promise<QueryOutput> {
  const [db, secrets] = await Promise.all([getDuckDb(), storageSecretsFor(sqlText, tokenCredential)]);

  signal.throwIfAborted();

  const connection = await db.connect();
  const cancel = () => void connection.cancelSent();

  signal.addEventListener("abort", cancel);

  try {
    if (secrets) {
      await connection.query(secrets);
    }

    signal.throwIfAborted();

    return await collectRows(await connection.send(sqlText, true), rowLimit);
  } finally {
    signal.removeEventListener("abort", cancel);
    await connection.close();
  }
}
