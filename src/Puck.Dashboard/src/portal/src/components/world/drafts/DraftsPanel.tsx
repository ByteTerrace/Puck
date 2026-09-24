import { useId, useState } from "react";
import { Alert, Badge, Button, Collapse, Group, Paper, Stack, Table, Text, Title, VisuallyHidden } from "@mantine/core";
import { RiArrowDownSLine, RiArrowUpSLine, RiDraftLine, RiErrorWarningLine } from "@remixicon/react";
import { StudioContext, useStudioCanDeleteDrafts, useStudioDrafts, useStudioIsDirty } from "../../../context/StudioContext";
import { EmptyState } from "../../../ui/EmptyState";
import { Kicker } from "../../../ui/Kicker";
import { Well } from "../../../ui/Well";
import { useStudioConfirmation } from "../StudioConfirmation";
import classes from "./DraftsPanel.module.css";

/**
 * The local draft library (`document/localDrafts.ts`): each draft is the set of source files an author changed over
 * the official build, kept in this browser. Bound entirely to `StudioContext`, whose context carries the listing as
 * of the last save or delete.
 * Folded by default; the listing stays in the markup while folded.
 */
export function DraftsPanel() {
  const actor = StudioContext.useActorRef();
  const isDirty = useStudioIsDirty();
  const canDelete = useStudioCanDeleteDrafts();
  const { drafts, error } = useStudioDrafts();
  const confirm = useStudioConfirmation();
  const [expanded, setExpanded] = useState(false);
  const panelId = useId();

  return (
    <Paper p="md">
      <Group justify="space-between" gap="sm" wrap="nowrap">
        <Stack gap={2}>
          <Kicker>This browser</Kicker>
          <Group gap="xs" align="center">
            <Title order={2} size="h4">Local drafts</Title>
            <Badge color="gray">{drafts.length} saved</Badge>
          </Group>
        </Stack>
        <Button
          size="xs"
          variant="subtle"
          color="gray"
          aria-expanded={expanded}
          aria-controls={panelId}
          rightSection={expanded ? <RiArrowUpSLine size={16} /> : <RiArrowDownSLine size={16} />}
          onClick={() => setExpanded(value => !value)}
        >
          {expanded ? "Hide drafts" : "Show drafts"}
        </Button>
      </Group>

      <Collapse expanded={expanded} keepMountedMode="display-none" id={panelId}>
        <Stack gap="sm" mt="md">
          {error && (
            <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Local drafts unreadable">
              {error}
            </Alert>
          )}
          {!error && drafts.length === 0 && (
            <EmptyState icon={<RiDraftLine size={22} />} title="No local drafts saved yet">
              Use Save draft in the header to keep the files you changed in this browser.
            </EmptyState>
          )}
          {drafts.length > 0 && (
            <Well>
              <Table.ScrollContainer minWidth={560}>
                <Table>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Draft</Table.Th>
                      <Table.Th>Document</Table.Th>
                      <Table.Th>Files</Table.Th>
                      <Table.Th>Revisions</Table.Th>
                      <Table.Th>Saved</Table.Th>
                      <Table.Th><VisuallyHidden>Actions</VisuallyHidden></Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {drafts.map((draft) => (
                      <Table.Tr key={draft.id}>
                        <Table.Td>
                          <Text size="sm" fw={600} className={classes.title}>{draft.title}</Text>
                          <Text size="xs" c="dimmed" ff="monospace">{draft.id}</Text>
                        </Table.Td>
                        <Table.Td><Text size="sm" ff="monospace">{draft.documentName}</Text></Table.Td>
                        <Table.Td><Text size="sm" ff="monospace">{Object.keys(draft.revisions[0]?.files ?? {}).length}</Text></Table.Td>
                        <Table.Td><Text size="sm" ff="monospace">{draft.revisions.length}</Text></Table.Td>
                        <Table.Td><Text size="sm" ff="monospace">{draft.revisions[0]?.savedAt.slice(0, 10) ?? "—"}</Text></Table.Td>
                        <Table.Td>
                          <Group gap={6} justify="flex-end" wrap="nowrap">
                            <Button
                              size="compact-xs"
                              variant="default"
                              aria-label={`Load ${draft.title}`}
                              onClick={async () => {
                                if (!isDirty || await confirm("Discard unsaved source edits and load this draft?")) actor.send({ type: "LOAD_DRAFT", id: draft.id });
                              }}
                            >
                              Load
                            </Button>
                            <Button
                              size="compact-xs"
                              variant="subtle"
                              color="red"
                              aria-label={`Delete ${draft.title}`}
                              disabled={!canDelete}
                              onClick={async () => {
                                if (await confirm(`Delete the local draft “${draft.title}” and its saved revisions?`)) actor.send({ type: "DELETE_DRAFT", id: draft.id });
                              }}
                            >
                              Delete
                            </Button>
                          </Group>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Well>
          )}
        </Stack>
      </Collapse>
    </Paper>
  );
}

export default DraftsPanel;
