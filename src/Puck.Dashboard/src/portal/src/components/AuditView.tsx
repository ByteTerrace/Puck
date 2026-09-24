import type { TokenCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";
import {
  Alert,
  Badge,
  Button,
  Group,
  type MantineColor,
  Paper,
  SegmentedControl,
  Skeleton,
  Stack,
  Table,
  Text,
} from "@mantine/core";
import { RiBarChartBoxLine, RiErrorWarningLine, RiHistoryLine } from "@remixicon/react";
import { useEffect, useState } from "react";
import { resolveStorageEndpoint } from "../clients/resolveStorageEndpoint";
import { navigateToSection } from "../shell/sectionStore";
import { EmptyState } from "../ui/EmptyState";
import { Kicker } from "../ui/Kicker";
import classes from "./Page.module.css";
import { PageHeader } from "./PageHeader";
import { Timestamp } from "./Timestamp";
import { Well } from "../ui/Well";
import { type AuditEvent, type AuditKind, auditJournal, initialAuditJournalState, JOURNAL_PREFIX } from "./auditJournal";
import { API_TOKEN_SCOPES, STORAGE_API_VERSION, STORAGE_TOKEN_SCOPES } from "./data/storage";

const KINDS: Record<AuditKind, { color: MantineColor; label: string }> = {
  created: { color: "jade", label: "created" },
  deleted: { color: "red", label: "deleted" },
  other: { color: "gray", label: "changed" },
  renamed: { color: "yellow", label: "renamed" },
};

const FILTERS = [
  { label: "All", value: "all" },
  { label: "Created", value: "created" },
  { label: "Deleted", value: "deleted" },
  { label: "Renamed", value: "renamed" },
];

const dayOf = (time: Date) => time.toLocaleDateString(undefined, { day: "numeric", month: "short", weekday: "short" });

function TimelineSkeleton() {
  return (
    <Stack aria-label="Loading your audit history" gap="xs" role="status">
      {[0, 1, 2, 3].map((row) => (
        <Skeleton height={28} key={row} />
      ))}
    </Stack>
  );
}

function Timeline({ events }: { events: AuditEvent[] }) {
  const days = Map.groupBy(events, (event) => dayOf(event.time));

  return (
    <Well>
      <Table.ScrollContainer minWidth={560}>
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Time</Table.Th>
              <Table.Th>Action</Table.Th>
              <Table.Th>Target</Table.Th>
              <Table.Th>Operation</Table.Th>
            </Table.Tr>
          </Table.Thead>
          {[...days].map(([day, dayEvents]) => (
            <Table.Tbody key={day}>
              <Table.Tr>
                <Table.Th colSpan={4} scope="rowgroup">
                  <Kicker c="dimmed">{day}</Kicker>
                </Table.Th>
              </Table.Tr>
              {dayEvents.map((event, index) => (
                <Table.Tr key={`${event.journalUrl}-${index}`}>
                  <Table.Td className={classes.nowrap}>
                    <Timestamp relative time={event.time} />
                  </Table.Td>
                  <Table.Td>
                    <Badge color={KINDS[event.kind].color}>{KINDS[event.kind].label}</Badge>
                  </Table.Td>
                  <Table.Td className={`${classes.data} ${classes.path}`}>{event.blobPath}</Table.Td>
                  <Table.Td c="dimmed" className={classes.data}>
                    {event.operation ?? ""}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          ))}
        </Table>
      </Table.ScrollContainer>
    </Well>
  );
}

/** Audit Trail: the read-only journal of changes to the user's files, newest first, grouped by day. */
export default function AuditView({
  tokenCredential,
  userObjectId,
}: {
  tokenCredential: TokenCredential;
  userObjectId: string;
}) {
  const [{ error, events, journalUrls }, setJournal] = useState(initialAuditJournalState);
  const [filter, setFilter] = useState("all");

  useEffect(() => {
    const subscription = auditJournal(
      {
        headers: async () => ({
          Authorization: `Bearer ${(await tokenCredential.getToken(STORAGE_TOKEN_SCOPES))!.token}`,
          "x-ms-version": STORAGE_API_VERSION,
        }),
        listJournalNames: async (endpoint) => {
          const names: string[] = [];

          for await (const blob of new BlobServiceClient(endpoint, tokenCredential)
            .getContainerClient(userObjectId)
            .listBlobsFlat({ prefix: JOURNAL_PREFIX })) {
            names.push(blob.name);
          }

          return names;
        },
        resolveEndpoint: () => resolveStorageEndpoint(tokenCredential, API_TOKEN_SCOPES),
        userObjectId: userObjectId,
      },
      async (url, headers) => {
        const response = await fetch(url, { headers });

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        return response.json();
      },
    ).subscribe(setJournal);

    return () => subscription.unsubscribe();
  }, [tokenCredential, userObjectId]);

  // Hands the newest journals to Cloud Storage's SQL editor, which picks the query up when it opens.
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

    navigateToSection("data");
  };

  const visible = (events ?? []).filter((event) => "all" === filter || event.kind === filter);

  return (
    <Stack gap="lg">
      <PageHeader
        actions={
          <Button
            disabled={0 === journalUrls.length}
            leftSection={<RiBarChartBoxLine size={16} />}
            onClick={analyzeWithSql}
            size="sm"
            variant="default"
          >
            Analyze with SQL
          </Button>
        }
        kicker="Account"
        title="Audit Trail"
      >
        Every change to your files, recorded automatically. Read-only — not even you can edit it.
      </PageHeader>
      <Paper p="md" withBorder>
        <Stack gap="md">
          <Group justify="space-between">
            <SegmentedControl aria-label="Show events" data={FILTERS} onChange={setFilter} size="xs" value={filter} />
            {events ? (
              <Text c="dimmed" className={classes.data}>
                {visible.length} of {events.length} events
              </Text>
            ) : null}
          </Group>
          {error ? (
            <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Could not load your audit history" variant="light">
              {error}
            </Alert>
          ) : undefined === events ? (
            <TimelineSkeleton />
          ) : 0 === visible.length ? (
            <EmptyState icon={<RiHistoryLine size={22} />} title={0 === events.length ? "No recorded events yet" : "Nothing matches"}>
              {0 === events.length
                ? "Uploads, deletions, and renames in your storage appear here as they happen."
                : "No recent event is of this kind. Choose All to see every event."}
            </EmptyState>
          ) : (
            <Timeline events={visible} />
          )}
        </Stack>
      </Paper>
    </Stack>
  );
}
