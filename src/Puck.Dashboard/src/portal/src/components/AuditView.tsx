import { TokenCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";
import {
  Alert,
  Box,
  Button,
  Code,
  Group,
  Loader,
  SegmentedControl,
  Stack,
  Text,
  Title,
} from "@mantine/core";
import { useCallback, useEffect, useState } from "react";
import { resolveStorageEndpoint } from "../clients/resolveStorageEndpoint";

const API_TOKEN_SCOPES = ["https://api.byteterrace.com/user_impersonation"];
const STORAGE_TOKEN_SCOPES = ["https://storage.azure.com/.default"];
const JOURNAL_PREFIX = "system/events/blob/";
const JOURNAL_FETCH_LIMIT = 60;

type AuditKind = "created" | "deleted" | "other" | "renamed";

interface AuditEvent {
  blobPath: string;
  journalUrl: string;
  kind: AuditKind;
  time: Date;
}

const KIND_STYLES: Record<AuditKind, { background: string; color: string; label: string }> = {
  created: { background: "#ebfbee", color: "#2f9e44", label: "created" },
  deleted: { background: "#fff5f5", color: "#e03131", label: "deleted" },
  other: { background: "#f1f3f5", color: "#868e96", label: "changed" },
  renamed: { background: "#fff9db", color: "#f08c00", label: "renamed" },
};

const kindOf = (eventType: string): AuditKind => {
  const type = eventType.toLowerCase();

  if (type.includes("created")) {
    return "created";
  }

  if (type.includes("deleted")) {
    return "deleted";
  }

  if (type.includes("renamed")) {
    return "renamed";
  }

  return "other";
};
// Journal payload shapes are parsed defensively: the writer serializes
// CloudEvent batches whose casing depends on serializer configuration.
const parseJournal = (raw: unknown, journalUrl: string): AuditEvent[] => {
  const record = raw as Record<string, unknown>;
  const items = (Array.isArray(raw)
    ? raw
    : ((record?.value ??
        record?.Value ??
        record?.items ??
        record?.Items) as unknown[])) ?? [];

  return items.flatMap((item) => {
    const event = item as Record<string, any>;
    const type = String(event?.type ?? event?.Type ?? "");
    const time = event?.time ?? event?.Time;
    const url = String(
      event?.data?.url ?? event?.Data?.url ?? event?.data?.Url ?? "",
    );
    let blobPath = "";

    try {
      blobPath = decodeURIComponent(
        new URL(url).pathname.split("/").slice(2).join("/"),
      );
    } catch {
      blobPath = url;
    }

    if (!type || !time) {
      return [];
    }

    return [
      {
        blobPath: blobPath,
        journalUrl: journalUrl,
        kind: kindOf(type),
        time: new Date(time),
      },
    ];
  });
};

export default function AuditView({
  tokenCredential,
  userObjectId,
}: {
  tokenCredential: TokenCredential;
  userObjectId: string;
}) {
  const [error, setError] = useState<string | undefined>();
  const [events, setEvents] = useState<AuditEvent[] | undefined>();
  const [filter, setFilter] = useState("all");
  const [journalUrls, setJournalUrls] = useState<string[]>([]);
  const [storageEndpoint, setStorageEndpoint] = useState<string>();

  useEffect(() => {
    resolveStorageEndpoint(tokenCredential, API_TOKEN_SCOPES).then(
      setStorageEndpoint,
      (e: unknown) => setError(e instanceof Error ? e.message : String(e)),
    );
  }, [tokenCredential]);

  const load = useCallback(async () => {
    if (!storageEndpoint) {
      return;
    }

    setError(undefined);

    try {
      const containerClient = new BlobServiceClient(
        storageEndpoint,
        tokenCredential,
      ).getContainerClient(userObjectId);
      const journalNames: string[] = [];

      for await (const blob of containerClient.listBlobsFlat({
        prefix: JOURNAL_PREFIX,
      })) {
        journalNames.push(blob.name);
      }

      // Journal names begin with a UTC timestamp, so name order is time order.
      const recent = journalNames.sort().reverse().slice(0, JOURNAL_FETCH_LIMIT);
      const accessToken = await tokenCredential.getToken(STORAGE_TOKEN_SCOPES);
      const headers = {
        Authorization: `Bearer ${accessToken!.token}`,
        "x-ms-version": "2025-01-05",
      };
      const parsed = await Promise.all(
        recent.map(async (name) => {
          const journalUrl = `${storageEndpoint}/${userObjectId}/${name}`;

          try {
            const response = await fetch(journalUrl, { headers });

            return response.ok
              ? parseJournal(await response.json(), journalUrl)
              : [];
          } catch {
            return [];
          }
        }),
      );

      setEvents(
        parsed.flat().sort((a, b) => b.time.getTime() - a.time.getTime()),
      );
      setJournalUrls(
        recent.map((name) => `${storageEndpoint}/${userObjectId}/${name}`),
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, [storageEndpoint, tokenCredential, userObjectId]);

  useEffect(() => {
    load();
  }, [load]);

  const analyzeWithSql = () => {
    const urls = journalUrls.slice(0, 20);

    if (0 === urls.length) {
      return;
    }

    try {
      sessionStorage.setItem(
        "byteterrace.pendingSql",
        `SELECT unnest(items, recursive := true)\nFROM read_json_auto([\n  ${urls.map((url) => `'${url}'`).join(",\n  ")}\n]);`,
      );
    } catch {
      return;
    }

    history.pushState(null, "", "/data");
    window.dispatchEvent(new PopStateEvent("popstate"));
  };

  const visible = (events ?? []).filter(
    (event) => "all" === filter || event.kind === filter,
  );
  const groups = visible.reduce<Record<string, AuditEvent[]>>(
    (accumulator, event) => {
      const day = event.time.toLocaleDateString(undefined, {
        day: "numeric",
        month: "short",
        weekday: "short",
      });

      (accumulator[day] ??= []).push(event);

      return accumulator;
    },
    {},
  );

  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-end">
        <Box>
          <Title order={3}>Audit</Title>
          <Text c="dimmed" size="sm">
            Every change to your files, recorded automatically. Read-only — not
            even you can edit it.
          </Text>
        </Box>
        <Button
          disabled={0 === journalUrls.length}
          onClick={analyzeWithSql}
          variant="default"
        >
          Analyze with SQL
        </Button>
      </Group>
      <SegmentedControl
        data={[
          { label: "All", value: "all" },
          { label: "Created", value: "created" },
          { label: "Deleted", value: "deleted" },
          { label: "Renamed", value: "renamed" },
        ]}
        onChange={setFilter}
        value={filter}
        w="fit-content"
      />
      {error ? (
        <Alert color="red" title="Could not load your audit history">
          {error}
        </Alert>
      ) : undefined === events ? (
        <Loader size="sm" />
      ) : 0 === visible.length ? (
        <Text c="dimmed">No recorded events yet.</Text>
      ) : (
        <Stack
          gap={0}
          style={{
            background: "var(--mantine-color-body, #ffffff)",
            border: "1px solid var(--mantine-color-default-border, #dee2e6)",
            borderRadius: 8,
          }}
        >
          {Object.entries(groups).map(([day, dayEvents]) => (
            <Box key={day}>
              <Text
                c="dimmed"
                fw={700}
                px="lg"
                py={8}
                size="xs"
                tt="uppercase"
              >
                {day}
              </Text>
              {dayEvents.map((event, index) => {
                const style = KIND_STYLES[event.kind];

                return (
                  <Group
                    gap="md"
                    key={`${event.journalUrl}-${index}`}
                    px="lg"
                    py={8}
                    wrap="nowrap"
                  >
                    <Box
                      style={{
                        alignItems: "center",
                        background: style.background,
                        borderRadius: 20,
                        color: style.color,
                        display: "flex",
                        flexShrink: 0,
                        fontSize: 11,
                        fontWeight: 700,
                        height: 22,
                        justifyContent: "center",
                        textTransform: "uppercase",
                        width: 68,
                      }}
                    >
                      {style.label}
                    </Box>
                    <Code style={{ flexGrow: 1 }}>{event.blobPath}</Code>
                    <Text
                      c="dimmed"
                      size="xs"
                      style={{ fontVariantNumeric: "tabular-nums" }}
                    >
                      {event.time.toLocaleTimeString(undefined, {
                        hour: "2-digit",
                        minute: "2-digit",
                      })}
                    </Text>
                  </Group>
                );
              })}
            </Box>
          ))}
        </Stack>
      )}
    </Stack>
  );
}
