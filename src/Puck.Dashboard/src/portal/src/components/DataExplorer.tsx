import { TokenCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";
import {
  Alert,
  Box,
  Button,
  Code,
  CopyButton,
  FileButton,
  Group,
  Loader,
  Menu,
  Modal,
  ScrollArea,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from "@mantine/core";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  isByteTerraceStorageUrl,
  resolveStorageEndpoint,
} from "../clients/resolveStorageEndpoint";
import SqlEditor from "./SqlEditor";

// TODO: Source the endpoint and scopes from public configuration instead of hardcoding them.
const API_TOKEN_SCOPES = ["https://api.byteterrace.com/user_impersonation"];
const STORAGE_TOKEN_SCOPES = ["https://storage.azure.com/.default"];
// TODO: Self-host the DuckDB bundles instead of pulling them from jsDelivr.
const DUCKDB_MODULE_URL =
  "https://cdn.jsdelivr.net/npm/@duckdb/duckdb-wasm@latest/+esm";
const SHARE_DURATIONS = [
  { hours: 1, label: "1 hour" },
  { hours: 24, label: "1 day" },
  { hours: 168, label: "7 days" },
];

interface BlobEntry {
  lastModified?: Date;
  name: string;
  sizeInBytes?: number;
}
interface PublicFileEntry {
  blobName: string;
  publicUrl: string;
  sizeInBytes?: number;
}
interface QueryOutput {
  columns: string[];
  rows: unknown[][];
}
// The API serializes property names in either casing depending on host
// configuration; read both.
const field = (record: any, name: string) =>
  record?.[name] ?? record?.[name[0].toUpperCase() + name.slice(1)];
const PENDING_SHARE_STORAGE_KEY = "byteterrace.pendingShare";
const QUERYABLE_READ_FUNCTIONS: Record<string, string> = {
  csv: "read_csv_auto",
  json: "read_json_auto",
  jsonl: "read_json_auto",
  ndjson: "read_json_auto",
  parquet: "read_parquet",
};

const buildQuerySql = (readFunction: string, url: string) =>
  `SELECT *\nFROM ${readFunction}('${url}')\nLIMIT 100;`;
const readFunctionFor = (name: string): string | undefined =>
  QUERYABLE_READ_FUNCTIONS[name.toLowerCase().split(".").pop() ?? ""];

const describeShareUri = (uri: string) => {
  try {
    const parsed = new URL(uri);

    return {
      expiresOn: parsed.searchParams.get("se"),
      name: decodeURIComponent(parsed.pathname.split("/").pop() ?? "file"),
    };
  } catch {
    return { expiresOn: null, name: "file" };
  }
};

let duckDbPromise: Promise<any> | undefined;

const getDuckDb = () =>
  (duckDbPromise ??= (async () => {
    const duckdb = await import(/* @vite-ignore */ DUCKDB_MODULE_URL);
    const bundle = await duckdb.selectBundle(duckdb.getJsDelivrBundles());
    const workerUrl = URL.createObjectURL(
      new Blob([`importScripts("${bundle.mainWorker}");`], {
        type: "text/javascript",
      }),
    );
    const db = new duckdb.AsyncDuckDB(
      new duckdb.VoidLogger(),
      new Worker(workerUrl),
    );

    await db.instantiate(bundle.mainModule, bundle.pthreadWorker);

    return db;
  })());

// A hard refresh loads the page uncontrolled by spec, and some browsers block
// service workers entirely — so the worker is only ever the fast path (lazy
// range reads); queries must never depend on it.
const hasServiceWorkerController = async (): Promise<boolean> => {
  if (!("serviceWorker" in navigator)) {
    return false;
  }

  await navigator.serviceWorker.ready;

  if (navigator.serviceWorker.controller) {
    return true;
  }

  return await new Promise<boolean>((resolve) => {
    const timeout = setTimeout(() => resolve(false), 2000);

    navigator.serviceWorker.addEventListener(
      "controllerchange",
      () => {
        clearTimeout(timeout);
        resolve(true);
      },
      { once: true },
    );
  });
};
const STORAGE_URL_PATTERN =
  /https:\/\/[^'"\s)]+\.blob\.core\.windows\.net\/[^'"\s)]+/g;

const registeredNameFor = (url: string, index: number): string => {
  const fileName = decodeURIComponent(
    url.split("?")[0].split("/").pop() ?? "file",
  ).replace(/[^A-Za-z0-9._-]/g, "_");

  return `remote_${index}_${fileName}`;
};
const formatCell = (value: unknown): string => {
  if (null === value || undefined === value) {
    return "";
  }

  if (value instanceof Date) {
    return value.toISOString();
  }

  return String(value);
};
const formatSize = (sizeInBytes?: number): string =>
  undefined === sizeInBytes ? "" : `${(sizeInBytes / 1024).toFixed(1)} KiB`;

export default function DataExplorer({
  tokenCredential,
  userObjectId,
}: {
  tokenCredential: TokenCredential;
  userObjectId: string;
}) {
  const [actionError, setActionError] = useState<string | undefined>();
  const [blobs, setBlobs] = useState<BlobEntry[] | undefined>();
  const [isRunning, setIsRunning] = useState(false);
  const [isUploading, setIsUploading] = useState(false);
  const [listError, setListError] = useState<string | undefined>();
  const [output, setOutput] = useState<QueryOutput | undefined>();
  const [queryError, setQueryError] = useState<string | undefined>();
  const [isDownloading, setIsDownloading] = useState(false);
  const [isSharing, setIsSharing] = useState(false);
  const [recipientInput, setRecipientInput] = useState("");
  const [share, setShare] = useState<
    { blobName: string; note: string; uri: string } | undefined
  >();
  const [isPublishing, setIsPublishing] = useState(false);
  const [publicFiles, setPublicFiles] = useState<PublicFileEntry[]>([]);
  const [publishTarget, setPublishTarget] = useState<string | undefined>();
  const [pendingShare, setPendingShare] = useState<string | undefined>(() => {
    try {
      return sessionStorage.getItem(PENDING_SHARE_STORAGE_KEY) ?? undefined;
    } catch {
      return undefined;
    }
  });
  const [shareDurationHours, setShareDurationHours] = useState(168);
  const [shareTarget, setShareTarget] = useState<string | undefined>();
  const [sql, setSql] = useState("");

  const [storageEndpoint, setStorageEndpoint] = useState<string>();

  useEffect(() => {
    resolveStorageEndpoint(tokenCredential, API_TOKEN_SCOPES).then(
      setStorageEndpoint,
      (e: unknown) =>
        setListError(e instanceof Error ? e.message : String(e)),
    );
  }, [tokenCredential]);

  const getContainerClient = useCallback(() => {
    if (!storageEndpoint) {
      throw new Error("Your storage account is still resolving — try again in a moment.");
    }

    return new BlobServiceClient(storageEndpoint, tokenCredential).getContainerClient(
      userObjectId,
    );
  }, [storageEndpoint, tokenCredential, userObjectId]);
  const loadBlobs = useCallback(async () => {
    setListError(undefined);

    try {
      const entries: BlobEntry[] = [];

      for await (const blob of getContainerClient().listBlobsFlat({
        prefix: "private/",
      })) {
        // Escrowed key material and provisioning-probe residue are
        // system-managed; keep them out of the UI.
        if (
          blob.name.startsWith("private/keys/") ||
          blob.name.startsWith("private/probe/")
        ) {
          continue;
        }

        entries.push({
          lastModified: blob.properties.lastModified,
          name: blob.name,
          sizeInBytes: blob.properties.contentLength,
        });
      }

      setBlobs(entries);
    } catch (e) {
      setListError(e instanceof Error ? e.message : String(e));
    }
  }, [getContainerClient]);

  useEffect(() => {
    if (!("serviceWorker" in navigator)) {
      return;
    }

    // The service worker pulls a fresh storage token from the page whenever it
    // needs one; it cannot hold state itself because idle termination wipes it.
    const listener = (event: MessageEvent) => {
      if ("storage-token-request" === event.data?.type && event.ports[0]) {
        const port = event.ports[0];

        tokenCredential.getToken(STORAGE_TOKEN_SCOPES).then(
          (accessToken) => port.postMessage({ token: accessToken?.token ?? null }),
          () => port.postMessage({ token: null }),
        );
      }
    };

    navigator.serviceWorker.addEventListener("message", listener);

    return () => navigator.serviceWorker.removeEventListener("message", listener);
  }, [tokenCredential]);
  const loadPublicFiles = useCallback(async () => {
    try {
      const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);
      const response = await fetch("/api/public-files", {
        headers: { Authorization: `Bearer ${apiToken!.token}` },
      });

      if (response.ok) {
        setPublicFiles(
          ((await response.json()) as unknown[]).map((record) => ({
            blobName: String(field(record, "blobName") ?? ""),
            publicUrl: String(field(record, "publicUrl") ?? ""),
            sizeInBytes: field(record, "sizeInBytes"),
          })),
        );
      }
    } catch {
      // The public-files section simply stays empty when unavailable.
    }
  }, [tokenCredential]);

  useEffect(() => {
    if (!storageEndpoint) {
      return;
    }

    loadBlobs();
    loadPublicFiles();
  }, [storageEndpoint, loadBlobs, loadPublicFiles]);
  useEffect(() => {
    try {
      const pendingSql = sessionStorage.getItem("byteterrace.pendingSql");

      if (pendingSql) {
        sessionStorage.removeItem("byteterrace.pendingSql");
        setSql(pendingSql);
      }
    } catch {
      // Storage unavailable; nothing to pick up.
    }
  }, []);

  const autoRanShare = useRef(false);

  // Share links clicked while the app is already open only change the hash;
  // the host captures them and announces via this event.
  useEffect(() => {
    const onShare = () => {
      try {
        const value =
          sessionStorage.getItem(PENDING_SHARE_STORAGE_KEY) ?? undefined;

        autoRanShare.current = false;
        setPendingShare(value);
      } catch {
        // Storage unavailable; nothing to pick up.
      }
    };

    window.addEventListener("byteterrace-share", onShare);

    return () => window.removeEventListener("byteterrace-share", onShare);
  }, []);

  const deleteBlob = useCallback(
    async (blobName: string) => {
      if (!window.confirm(`Delete ${blobName}? This cannot be undone.`)) {
        return;
      }

      setActionError(undefined);

      try {
        await getContainerClient().deleteBlob(blobName);
        await loadBlobs();
      } catch (e) {
        setActionError(e instanceof Error ? e.message : String(e));
      }
    },
    [getContainerClient, loadBlobs],
  );
  const insertQueryFor = (blobName: string) => {
    const readFunction = readFunctionFor(blobName);

    if (readFunction) {
      setSql(
        buildQuerySql(
          readFunction,
          `${storageEndpoint ?? ""}/${userObjectId}/${blobName}`,
        ),
      );
    }
  };
  const runQuery = useCallback(async (sqlText: string) => {
    setIsRunning(true);
    setOutput(undefined);
    setQueryError(undefined);

    try {
      const db = await getDuckDb();
      let effectiveSql = sqlText;

      if (!(await hasServiceWorkerController())) {
        // Buffered fallback: fetch each referenced file with page-side auth
        // (page fetches CAN attach headers), register the bytes with DuckDB,
        // and point the query at the registered names instead of the URLs.
        const urls = [...new Set(sqlText.match(STORAGE_URL_PATTERN) ?? [])];

        if (0 < urls.length) {
          const accessToken =
            await tokenCredential.getToken(STORAGE_TOKEN_SCOPES);

          for (const [index, url] of urls.entries()) {
            const response = await fetch(url, {
              headers: {
                Authorization: `Bearer ${accessToken!.token}`,
                "x-ms-version": "2025-01-05",
              },
            });

            if (!response.ok) {
              const errorCode = response.headers.get("x-ms-error-code");

              throw new Error(
                `Could not open ${url.split("?")[0].split("/").pop()} (HTTP ${response.status}${errorCode ? `, ${errorCode}` : ""}).`,
              );
            }

            const registeredName = registeredNameFor(url, index);

            try {
              await db.dropFile(registeredName);
            } catch {
              // Not registered yet.
            }

            await db.registerFileBuffer(
              registeredName,
              new Uint8Array(await response.arrayBuffer()),
            );
            effectiveSql = effectiveSql.split(url).join(registeredName);
          }
        }
      }

      const connection = await db.connect();

      try {
        const result = await connection.query(effectiveSql);
        const columns: string[] = result.schema.fields.map(
          (field: { name: string }) => field.name,
        );

        setOutput({
          columns: columns,
          rows: result
            .toArray()
            .map((row: Record<string, unknown>) =>
              columns.map((column) => row[column]),
            ),
        });
      } finally {
        await connection.close();
      }
    } catch (e) {
      let message = e instanceof Error ? e.message : String(e);

      // DuckDB reports every non-2xx response as "No files found"; probe the
      // referenced URL through the same service-worker path to surface the
      // actual HTTP status.
      const referencedUrl = sqlText.match(
        /https:\/\/[^'"\s)]+\.blob\.core\.windows\.net\/[^'"\s)]+/,
      );

      if (referencedUrl && /no files found/i.test(message)) {
        try {
          const probe = await fetch(referencedUrl[0], { method: "HEAD" });
          const errorCode = probe.headers.get("x-ms-error-code");

          message += ` — direct check of the file returned HTTP ${probe.status}${errorCode ? ` (${errorCode})` : ""}.`;
        } catch {
          message += " — direct check of the file failed at the network level.";
        }
      }

      setQueryError(message);
    } finally {
      setIsRunning(false);
    }
  }, [tokenCredential]);

  // A queryable shared file greets the recipient with its data, not a button.
  useEffect(() => {
    if (!pendingShare || autoRanShare.current) {
      return;
    }

    const readFunction = readFunctionFor(describeShareUri(pendingShare).name);

    if (!readFunction) {
      return;
    }

    autoRanShare.current = true;

    const sqlText = buildQuerySql(readFunction, pendingShare);

    setSql(sqlText);
    runQuery(sqlText);
  }, [pendingShare, runQuery]);
  const shareBlob = useCallback(
    async (blobName: string, hours: number) => {
      setActionError(undefined);
      setShare(undefined);

      try {
        const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);
        const response = await fetch("/api/generate-blob-sas-uri", {
          body: JSON.stringify({
            BlobName: blobName,
            ContainerName: userObjectId,
            ExpiresOn: new Date(Date.now() + hours * 3600000).toISOString(),
            // An empty value opts out of the service's default preauthorized
            // agent, which would otherwise make the link unusable by anonymous
            // recipients.
            PreauthorizedAgentObjectId: "",
          }),
          headers: {
            Authorization: `Bearer ${apiToken!.token}`,
            "Content-Type": "application/json",
          },
          method: "POST",
        });

        if (!response.ok) {
          throw new Error(`The share request failed (HTTP ${response.status}).`);
        }

        const body = await response.json();
        const uri: string | undefined = body.sasUri ?? body.SasUri;

        if (!uri) {
          throw new Error("The share request returned no link.");
        }

        setShare({
          blobName: blobName,
          note: "Anyone with this link can read the file until it expires.",
          uri: uri,
        });

        try {
          await navigator.clipboard.writeText(uri);
        } catch {
          // Clipboard access can be denied; the link is still shown below.
        }
      } catch (e) {
        setActionError(e instanceof Error ? e.message : String(e));
      }
    },
    [tokenCredential, userObjectId],
  );
  const shareWithUser = useCallback(async () => {
    if (!shareTarget) {
      return;
    }

    setActionError(undefined);
    setIsSharing(true);

    try {
      const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);
      const expiresOn = new Date(Date.now() + shareDurationHours * 3600000);
      const response = await fetch("/api/shares", {
        body: JSON.stringify({
          BlobName: shareTarget,
          ExpiresOn: expiresOn.toISOString(),
          RecipientIdentifier: recipientInput.trim(),
        }),
        headers: {
          Authorization: `Bearer ${apiToken!.token}`,
          "Content-Type": "application/json",
        },
        method: "POST",
      });

      if (404 === response.status) {
        throw new Error("No user was found with that email address.");
      }

      if (!response.ok) {
        throw new Error(`The share request failed (HTTP ${response.status}).`);
      }

      const body = await response.json();
      const recipientName: string =
        field(body, "recipientDisplayName") ?? recipientInput;
      const uri: string | undefined = field(body, "sasUri");

      if (!uri) {
        throw new Error("The share request returned no link.");
      }

      const portalLink = `${location.origin}/data#share=${encodeURIComponent(uri)}`;

      setShare({
        blobName: shareTarget,
        note: `Send this link to ${recipientName} — it opens the file right here in the portal after they sign in, and it only works for them. It expires ${expiresOn.toLocaleString()} and cannot be revoked early.`,
        uri: portalLink,
      });
      setRecipientInput("");
      setShareTarget(undefined);

      try {
        await navigator.clipboard.writeText(portalLink);
      } catch {
        // Clipboard access can be denied; the link is still shown below.
      }
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    } finally {
      setIsSharing(false);
    }
  }, [recipientInput, shareDurationHours, shareTarget, tokenCredential]);
  const downloadFromUrl = useCallback(async (url: string) => {
    setActionError(undefined);

    if (!isByteTerraceStorageUrl(url)) {
      setActionError(
        "That does not look like a ByteTerrace share link — check the link you received.",
      );

      return;
    }

    setIsDownloading(true);

    try {
      // Auth is attached directly so downloads never depend on the service
      // worker (which skips requests that already carry Authorization).
      const accessToken = await tokenCredential.getToken(STORAGE_TOKEN_SCOPES);
      const response = await fetch(url, {
        headers: {
          Authorization: `Bearer ${accessToken!.token}`,
          "x-ms-version": "2025-01-05",
        },
      });

      if (!response.ok) {
        const errorCode = response.headers.get("x-ms-error-code");

        throw new Error(
          `The file could not be opened (HTTP ${response.status}${errorCode ? `, ${errorCode}` : ""}). It may not be shared with you, or sharing was revoked.`,
        );
      }

      const anchor = document.createElement("a");

      anchor.download = decodeURIComponent(
        url.split("?")[0].split("/").pop() ?? "download",
      );
      anchor.href = URL.createObjectURL(await response.blob());
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    } finally {
      setIsDownloading(false);
    }
  }, [tokenCredential]);
  const publishBlob = useCallback(async () => {
    if (!publishTarget) {
      return;
    }

    setActionError(undefined);
    setIsPublishing(true);

    try {
      const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);
      const response = await fetch("/api/publish", {
        body: JSON.stringify({ BlobName: publishTarget }),
        headers: {
          Authorization: `Bearer ${apiToken!.token}`,
          "Content-Type": "application/json",
        },
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`Publishing failed (HTTP ${response.status}).`);
      }

      const body = await response.json();
      const publicUrl = String(field(body, "publicUrl") ?? "");

      setShare({
        blobName: `${publishTarget} is now public (address copied)`,
        note: "Anyone on the internet can download it from this address. Unpublishing moves it back to your private files, though cached copies may stay reachable for a short while.",
        uri: publicUrl,
      });
      setPublishTarget(undefined);

      try {
        await navigator.clipboard.writeText(publicUrl);
      } catch {
        // Clipboard access can be denied; the address is still shown.
      }

      await Promise.all([loadBlobs(), loadPublicFiles()]);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : String(e));
    } finally {
      setIsPublishing(false);
    }
  }, [loadBlobs, loadPublicFiles, publishTarget, tokenCredential]);
  const unpublishBlob = useCallback(
    async (blobName: string) => {
      setActionError(undefined);

      try {
        const apiToken = await tokenCredential.getToken(API_TOKEN_SCOPES);
        const response = await fetch("/api/unpublish", {
          body: JSON.stringify({ BlobName: blobName }),
          headers: {
            Authorization: `Bearer ${apiToken!.token}`,
            "Content-Type": "application/json",
          },
          method: "POST",
        });

        if (!response.ok) {
          throw new Error(`Unpublishing failed (HTTP ${response.status}).`);
        }

        setShare({
          blobName: `${blobName} is private again`,
          note: "It is back under your files. Copies cached at the edge may stay reachable for a short while.",
          uri: "",
        });
        await Promise.all([loadBlobs(), loadPublicFiles()]);
      } catch (e) {
        setActionError(e instanceof Error ? e.message : String(e));
      }
    },
    [loadBlobs, loadPublicFiles, tokenCredential],
  );
  const upload = useCallback(
    async (file: File | null) => {
      if (!file) {
        return;
      }

      setActionError(undefined);
      setIsUploading(true);

      try {
        await getContainerClient()
          .getBlockBlobClient(`private/${file.name}`)
          .uploadData(file, {
            blobHTTPHeaders: {
              blobContentType: file.type || "application/octet-stream",
            },
          });
        await loadBlobs();
      } catch (e) {
        setActionError(e instanceof Error ? e.message : String(e));
      } finally {
        setIsUploading(false);
      }
    },
    [getContainerClient, loadBlobs],
  );

  return (
    <Stack gap="lg">
      <Group justify="space-between">
        <Box>
          <Title order={3}>Your data</Title>
          <Text c="dimmed" size="sm">
            Files under <Code>private/</Code> in your storage container.
            Queries run entirely in your browser.
          </Text>
        </Box>
        <FileButton onChange={upload}>
          {(props) => (
            <Button {...props} loading={isUploading}>
              Upload
            </Button>
          )}
        </FileButton>
      </Group>
      {actionError ? (
        <Alert
          color="red"
          onClose={() => setActionError(undefined)}
          title="Something went wrong"
          withCloseButton
        >
          {actionError}
        </Alert>
      ) : null}
      {share ? (
        <Alert
          color="teal"
          onClose={() => setShare(undefined)}
          title={
            share.uri
              ? `Share link for ${share.blobName} (copied to clipboard)`
              : share.blobName
          }
          withCloseButton
        >
          {share.uri ? (
            <Group align="flex-end" wrap="nowrap">
              <TextInput
                onFocus={(event) => event.currentTarget.select()}
                readOnly
                style={{ flex: 1 }}
                value={share.uri}
              />
              <CopyButton value={share.uri}>
                {({ copied, copy }) => (
                  <Button color={copied ? "teal" : undefined} onClick={copy}>
                    {copied ? "Copied" : "Copy link"}
                  </Button>
                )}
              </CopyButton>
            </Group>
          ) : null}
          <Text c="dimmed" mt={4} size="xs">
            {share.note}
          </Text>
        </Alert>
      ) : null}
      {listError ? (
        <Alert color="red" title="Could not list your files">
          {listError}
        </Alert>
      ) : undefined === blobs ? (
        <Loader size="sm" />
      ) : 0 === blobs.length ? (
        <Text c="dimmed">No files found.</Text>
      ) : (
        <Table highlightOnHover withTableBorder>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Size</Table.Th>
              <Table.Th>Modified</Table.Th>
              <Table.Th></Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {blobs.map((blob) => (
              <Table.Tr key={blob.name}>
                <Table.Td>
                  <Code>{blob.name}</Code>
                </Table.Td>
                <Table.Td>{formatSize(blob.sizeInBytes)}</Table.Td>
                <Table.Td>
                  {blob.lastModified
                    ? blob.lastModified.toLocaleDateString(undefined, {
                        day: "numeric",
                        month: "short",
                      })
                    : ""}
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end" wrap="nowrap">
                    {readFunctionFor(blob.name) ? (
                      <Button
                        onClick={() => insertQueryFor(blob.name)}
                        size="compact-xs"
                        variant="light"
                      >
                        Query
                      </Button>
                    ) : null}
                    <Menu position="bottom-end" withinPortal>
                      <Menu.Target>
                        <Button size="compact-xs" variant="light">
                          Share
                        </Button>
                      </Menu.Target>
                      <Menu.Dropdown>
                        <Menu.Label>Anyone with the link, expires after</Menu.Label>
                        {SHARE_DURATIONS.map((duration) => (
                          <Menu.Item
                            key={duration.hours}
                            onClick={() => shareBlob(blob.name, duration.hours)}
                          >
                            {duration.label}
                          </Menu.Item>
                        ))}
                        <Menu.Divider />
                        <Menu.Item onClick={() => setShareTarget(blob.name)}>
                          Specific user…
                        </Menu.Item>
                      </Menu.Dropdown>
                    </Menu>
                    <Button
                      onClick={() => setPublishTarget(blob.name)}
                      size="compact-xs"
                      variant="light"
                    >
                      Publish
                    </Button>
                    <Button
                      color="red"
                      onClick={() => deleteBlob(blob.name)}
                      size="compact-xs"
                      variant="light"
                    >
                      Delete
                    </Button>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
      {pendingShare
        ? (() => {
            const described = describeShareUri(pendingShare);

            return (
              <Alert
                color="blue"
                onClose={() => {
                  setPendingShare(undefined);

                  try {
                    sessionStorage.removeItem(PENDING_SHARE_STORAGE_KEY);
                  } catch {
                    // Nothing to clean up if storage is unavailable.
                  }
                }}
                title={`A file was shared with you: ${described.name}`}
                withCloseButton
              >
                <Group>
                  {readFunctionFor(described.name) ? (
                    <Button
                      loading={isRunning}
                      onClick={() => {
                        const sqlText = buildQuerySql(
                          readFunctionFor(described.name)!,
                          pendingShare,
                        );

                        setSql(sqlText);
                        runQuery(sqlText);
                      }}
                      size="compact-sm"
                      variant="light"
                    >
                      Query
                    </Button>
                  ) : null}
                  <Button
                    loading={isDownloading}
                    onClick={() => downloadFromUrl(pendingShare)}
                    size="compact-sm"
                    variant="light"
                  >
                    Download
                  </Button>
                  {described.expiresOn ? (
                    <Text c="dimmed" size="xs">
                      Expires {new Date(described.expiresOn).toLocaleString()}
                    </Text>
                  ) : null}
                </Group>
              </Alert>
            );
          })()
        : null}
      {0 < publicFiles.length ? (
        <Box>
          <Title order={4}>Your public files</Title>
          <Table highlightOnHover withTableBorder>
            <Table.Tbody>
              {publicFiles.map((file) => (
                <Table.Tr key={file.blobName}>
                  <Table.Td>
                    <Code>{file.blobName}</Code>
                  </Table.Td>
                  <Table.Td>{formatSize(file.sizeInBytes)}</Table.Td>
                  <Table.Td>
                    <a href={file.publicUrl} rel="noreferrer" target="_blank">
                      {file.publicUrl.replace("https://", "")}
                    </a>
                  </Table.Td>
                  <Table.Td>
                    <Group gap="xs" justify="flex-end" wrap="nowrap">
                      <CopyButton value={file.publicUrl}>
                        {({ copied, copy }) => (
                          <Button
                            color={copied ? "teal" : undefined}
                            onClick={copy}
                            size="compact-xs"
                            variant="light"
                          >
                            {copied ? "Copied" : "Copy address"}
                          </Button>
                        )}
                      </CopyButton>
                      <Button
                        color="red"
                        onClick={() => unpublishBlob(file.blobName)}
                        size="compact-xs"
                        variant="light"
                      >
                        Unpublish
                      </Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Box>
      ) : null}
      <Box>
        <Title order={4}>SQL</Title>
        <SqlEditor
          files={[
            ...blobs?.map((blob) => ({
              label: blob.name,
              url: `${storageEndpoint ?? ""}/${userObjectId}/${blob.name}`,
            })) ?? [],
          ]}
          onChange={setSql}
          value={sql}
        />
        <Group mt="sm">
          <Button
            disabled={0 === sql.trim().length}
            loading={isRunning}
            onClick={() => runQuery(sql)}
          >
            Run
          </Button>
        </Group>
      </Box>
      {queryError ? (
        <Alert color="red" title="Query failed">
          {queryError}
        </Alert>
      ) : null}
      {output ? (
        <ScrollArea>
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                {output.columns.map((column) => (
                  <Table.Th key={column}>{column}</Table.Th>
                ))}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {output.rows.map((row, rowIndex) => (
                <Table.Tr key={rowIndex}>
                  {row.map((cell, cellIndex) => (
                    <Table.Td key={cellIndex}>{formatCell(cell)}</Table.Td>
                  ))}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          <Text c="dimmed" mt="xs" size="sm">
            {output.rows.length} row(s)
          </Text>
        </ScrollArea>
      ) : null}
      <Modal
        onClose={() => setPublishTarget(undefined)}
        opened={undefined !== publishTarget}
        title={`Publish ${publishTarget ?? ""}`}
      >
        <Stack>
          <Alert color="yellow">
            This <b>moves</b> the file out of your private storage. Anyone on
            the internet will be able to download it, and it will leave your
            files above until you unpublish it.
          </Alert>
          <Box>
            <Text fw={600} size="sm">
              Its public address
            </Text>
            <Code block>
              {`byteterrace.com/public/${userObjectId}/${(publishTarget ?? "").replace(/^private\//, "")}`}
            </Code>
          </Box>
          <Group justify="flex-end">
            <Button
              onClick={() => setPublishTarget(undefined)}
              variant="default"
            >
              Cancel
            </Button>
            <Button loading={isPublishing} onClick={publishBlob}>
              Publish
            </Button>
          </Group>
        </Stack>
      </Modal>
      <Modal
        onClose={() => setShareTarget(undefined)}
        opened={undefined !== shareTarget}
        title={`Share ${shareTarget ?? ""} with a user`}
      >
        <Stack>
          <TextInput
            label="Email or sign-in name"
            onChange={(event) => setRecipientInput(event.currentTarget.value)}
            placeholder="user@example.com"
            value={recipientInput}
          />
          <Select
            allowDeselect={false}
            data={SHARE_DURATIONS.map((duration) => ({
              label: duration.label,
              value: String(duration.hours),
            }))}
            label="Expires after"
            onChange={(value) => setShareDurationHours(Number(value ?? 168))}
            value={String(shareDurationHours)}
          />
          <Text c="dimmed" size="xs">
            The link only works for that person while they are signed in —
            forwarding it to anyone else does nothing. It cannot be revoked
            early, so pick the shortest expiry that works.
          </Text>
          <Group justify="flex-end">
            <Button
              disabled={0 === recipientInput.trim().length}
              loading={isSharing}
              onClick={shareWithUser}
            >
              Share
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
