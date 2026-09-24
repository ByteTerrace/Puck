import { isByteTerraceStorageUrl } from "../../clients/resolveStorageEndpoint";
import { sqlStringLiteral, STORAGE_API_VERSION } from "./storage";

const URL_PATTERN = /https:\/\/[^'"\s)]+/g;

/** The ByteTerrace storage accounts whose URLs `sqlText` names, as origins, each once and in order. */
export const storageOriginsIn = (sqlText: string): string[] => [
  ...new Set((sqlText.match(URL_PATTERN) ?? []).filter(isByteTerraceStorageUrl).map((url) => new URL(url).origin)),
];

/**
 * One HTTP secret per storage account the query names, so the engine reads those files with the user's token and
 * sends it nowhere else: a secret applies only to URLs under its `SCOPE`. `duckdb_secrets()` shows `BEARER_TOKEN`
 * redacted; the extra headers are not secret.
 *
 * - `x-ms-version`: Azure Storage refuses OAuth requests that do not name an API version.
 * - `Cache-Control: no-cache`: storage responses carry no `Cache-Control`, so the browser would otherwise answer a
 *   read of a file that has since been overwritten from its heuristic cache.
 */
export const storageSecretsSql = (origins: readonly string[], accessToken: string): string =>
  origins
    .map(
      (origin) =>
        `CREATE OR REPLACE SECRET ${secretNameFor(origin)} (TYPE http, BEARER_TOKEN ${sqlStringLiteral(accessToken)}, ` +
        `EXTRA_HTTP_HEADERS MAP {'x-ms-version': ${sqlStringLiteral(STORAGE_API_VERSION)}, 'Cache-Control': 'no-cache'}, ` +
        `SCOPE ${sqlStringLiteral(`${origin}/`)})`,
    )
    .join(";\n");

// `bytrcstp001.blob.core.windows.net` names the secret `storage_bytrcstp001`: the account name is lowercase letters
// and digits, so it is a plain identifier.
const secretNameFor = (origin: string) => `storage_${new URL(origin).host.split(".")[0]}`;
