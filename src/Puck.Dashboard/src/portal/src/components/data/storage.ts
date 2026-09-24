import type { TokenCredential } from "@azure/identity";
import { BlobServiceClient, type ContainerClient } from "@azure/storage-blob";
import { isByteTerraceStorageUrl } from "../../clients/resolveStorageEndpoint";

// TODO: Source the endpoint and scopes from public configuration instead of hardcoding them.
export const API_TOKEN_SCOPES = ["https://api.byteterrace.com/user_impersonation"];
export const STORAGE_TOKEN_SCOPES = ["https://storage.azure.com/.default"];
export const STORAGE_API_VERSION = "2025-01-05";
export const PENDING_SHARE_STORAGE_KEY = "byteterrace.pendingShare";
export const PENDING_SQL_STORAGE_KEY = "byteterrace.pendingSql";
export const SHARE_DURATIONS = [
  { hours: 1, label: "1 hour" },
  { hours: 24, label: "1 day" },
  { hours: 168, label: "7 days" },
];

export interface BlobEntry {
  lastModified?: Date;
  name: string;
  sizeInBytes?: number;
}

export interface PublicFileEntry {
  blobName: string;
  publicUrl: string;
  sizeInBytes?: number;
}

/** The outcome of a share, publish, or unpublish: what happened, the address it produced (if any), and its terms. */
export interface ShareNotice {
  note: string;
  title: string;
  uri: string;
}

const QUERYABLE_READ_FUNCTIONS: Record<string, string> = {
  csv: "read_csv_auto",
  json: "read_json_auto",
  jsonl: "read_json_auto",
  ndjson: "read_json_auto",
  parquet: "read_parquet",
};

// The API serializes property names in either casing depending on host configuration; read both.
const field = (record: any, name: string) => record?.[name] ?? record?.[name[0].toUpperCase() + name.slice(1)];

export const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : String(error));

export const NOT_A_SHARE_LINK = "That does not look like a ByteTerrace share link — check the link you received.";

/**
 * A SQL string literal for `text`: quoted, with every embedded quote doubled, so a file name or link containing `'`
 * stays one value instead of ending the literal early and running the rest as SQL.
 */
export const sqlStringLiteral = (text: string) => `'${text.replaceAll("'", "''")}'`;

/** The starter query for one file. The SQL editor shows it, so it stays readable, editable text. */
export const buildQuerySql = (readFunction: string, url: string) =>
  `SELECT *\nFROM ${readFunction}(${sqlStringLiteral(url)})\nLIMIT 100;`;

/** The DuckDB reader for a file name's extension, or undefined when the file is not queryable. */
export const readFunctionFor = (name: string): string | undefined =>
  QUERYABLE_READ_FUNCTIONS[name.toLowerCase().split(".").pop() ?? ""];

/**
 * What a share link names. A link arrives in a URL fragment anyone can write, so `isByteTerrace` says whether it points
 * at ByteTerrace storage at all; nothing queries or downloads a link that does not.
 */
export const describeShareUri = (uri: string) => {
  try {
    const parsed = new URL(uri);

    return {
      expiresOn: parsed.searchParams.get("se"),
      isByteTerrace: isByteTerraceStorageUrl(uri),
      name: decodeURIComponent(parsed.pathname.split("/").pop() ?? "file"),
    };
  } catch {
    return { expiresOn: null, isByteTerrace: false, name: "file" };
  }
};

export const formatSize = (sizeInBytes?: number): string =>
  undefined === sizeInBytes ? "" : `${(sizeInBytes / 1024).toFixed(1)} KiB`;

/** A blob's URL, each path segment percent-encoded so a name containing `#`, `?`, `%`, or a space still names the blob. */
export const blobUrl = (storageEndpoint: string | undefined, userObjectId: string, blobName: string) =>
  `${storageEndpoint ?? ""}/${userObjectId}/${blobName.split("/").map(encodeURIComponent).join("/")}`;

export const containerClientFor = (storageEndpoint: string, tokenCredential: TokenCredential, userObjectId: string): ContainerClient =>
  new BlobServiceClient(storageEndpoint, tokenCredential).getContainerClient(userObjectId);

/** Copies text to the clipboard where the browser allows it; the caller shows the text either way. */
export const tryCopy = async (text: string) => {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Clipboard access can be denied; the text is still shown.
  }
};

export async function listPrivateFiles(containerClient: ContainerClient): Promise<BlobEntry[]> {
  const entries: BlobEntry[] = [];

  for await (const blob of containerClient.listBlobsFlat({ prefix: "private/" })) {
    // Escrowed key material and provisioning-probe residue are system-managed; keep them out of the UI.
    if (blob.name.startsWith("private/keys/") || blob.name.startsWith("private/probe/")) {
      continue;
    }

    entries.push({
      lastModified: blob.properties.lastModified,
      name: blob.name,
      sizeInBytes: blob.properties.contentLength,
    });
  }

  return entries;
}

async function callApi(tokenCredential: TokenCredential, path: string, body?: unknown): Promise<Response> {
  const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);

  return await fetch(
    path,
    undefined === body
      ? { headers: { Authorization: `Bearer ${apiToken!.token}` } }
      : {
          body: JSON.stringify(body),
          headers: { Authorization: `Bearer ${apiToken!.token}`, "Content-Type": "application/json" },
          method: "POST",
        },
  );
}

/** The user's published files; empty when the service cannot answer, since the section is optional. */
export async function listPublicFiles(tokenCredential: TokenCredential): Promise<PublicFileEntry[]> {
  try {
    const response = await callApi(tokenCredential, "/api/public-files");

    if (!response.ok) {
      return [];
    }

    return ((await response.json()) as unknown[]).map((record) => ({
      blobName: String(field(record, "blobName") ?? ""),
      publicUrl: String(field(record, "publicUrl") ?? ""),
      sizeInBytes: field(record, "sizeInBytes"),
    }));
  } catch {
    return [];
  }
}

/** An anonymous read link for one file, valid for `hours`. */
export async function requestShareLink(
  tokenCredential: TokenCredential,
  userObjectId: string,
  blobName: string,
  hours: number,
): Promise<string> {
  const response = await callApi(tokenCredential, "/api/generate-blob-sas-uri", {
    BlobName: blobName,
    ContainerName: userObjectId,
    ExpiresOn: new Date(Date.now() + hours * 3600000).toISOString(),
    // An empty value opts out of the service's default preauthorized agent, which would otherwise make the link
    // unusable by anonymous recipients.
    PreauthorizedAgentObjectId: "",
  });

  if (!response.ok) {
    throw new Error(`The share request failed (HTTP ${response.status}).`);
  }

  const uri: string | undefined = field(await response.json(), "sasUri");

  if (!uri) {
    throw new Error("The share request returned no link.");
  }

  return uri;
}

/** A recipient-bound link for one file, opened through the portal. */
export async function requestUserShare(
  tokenCredential: TokenCredential,
  blobName: string,
  recipient: string,
  expiresOn: Date,
): Promise<{ recipientName: string; uri: string }> {
  const response = await callApi(tokenCredential, "/api/shares", {
    BlobName: blobName,
    ExpiresOn: expiresOn.toISOString(),
    RecipientIdentifier: recipient,
  });

  if (404 === response.status) {
    throw new Error("No user was found with that email address.");
  }

  if (!response.ok) {
    throw new Error(`The share request failed (HTTP ${response.status}).`);
  }

  const body = await response.json();
  const uri: string | undefined = field(body, "sasUri");

  if (!uri) {
    throw new Error("The share request returned no link.");
  }

  return { recipientName: field(body, "recipientDisplayName") ?? recipient, uri: uri };
}

/** Moves a private file to the public container and returns its public address. */
export async function publishFile(tokenCredential: TokenCredential, blobName: string): Promise<string> {
  const response = await callApi(tokenCredential, "/api/publish", { BlobName: blobName });

  if (!response.ok) {
    throw new Error(`Publishing failed (HTTP ${response.status}).`);
  }

  return String(field(await response.json(), "publicUrl") ?? "");
}

/** Moves a published file back under the user's private files. */
export async function unpublishFile(tokenCredential: TokenCredential, blobName: string): Promise<void> {
  const response = await callApi(tokenCredential, "/api/unpublish", { BlobName: blobName });

  if (!response.ok) {
    throw new Error(`Unpublishing failed (HTTP ${response.status}).`);
  }
}

/** Downloads a shared file with the viewer's own storage token and hands it to the browser as a save. */
export async function downloadSharedFile(tokenCredential: TokenCredential, url: string): Promise<void> {
  if (!describeShareUri(url).isByteTerrace) {
    throw new Error(NOT_A_SHARE_LINK);
  }

  // Storage responses carry no Cache-Control, so the browser would otherwise reuse a stale copy of a file that has
  // since been overwritten; `no-cache` revalidates it against its ETag.
  const accessToken = await tokenCredential.getToken(STORAGE_TOKEN_SCOPES);
  const response = await fetch(url, {
    cache: "no-cache",
    headers: { Authorization: `Bearer ${accessToken!.token}`, "x-ms-version": STORAGE_API_VERSION },
  });

  if (!response.ok) {
    const errorCode = response.headers.get("x-ms-error-code");

    throw new Error(
      `The file could not be opened (HTTP ${response.status}${errorCode ? `, ${errorCode}` : ""}). It may not be shared with you, or sharing was revoked.`,
    );
  }

  const anchor = document.createElement("a");

  anchor.download = decodeURIComponent(url.split("?")[0].split("/").pop() ?? "download");
  anchor.href = URL.createObjectURL(await response.blob());
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}
