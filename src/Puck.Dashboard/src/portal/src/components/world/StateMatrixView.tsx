import { useEffect, useState } from "react";
import { Badge, Button, Code, Group, Paper, ScrollArea, Stack, Table, Text, TextInput, Title } from "@mantine/core";
import { RiLockLine, RiRestartLine, RiTableLine } from "@remixicon/react";
import { StudioContext, useStudioCanDrivePreview, useStudioComposition, useStudioPreview } from "../../context/StudioContext";
import { listStateRows, type CellsOfDomain, type WorldStateRow } from "../../authoring/documentTools";
import { cellNumber, cellText, sameCellValue, type RowInfo } from "../../native/engineTypes";
import { EmptyState } from "../../ui/EmptyState";
import { Kicker } from "../../ui/Kicker";
import classes from "./StateMatrixView.module.css";
import { Well } from "../../ui/Well";

function isScalarLive(row: RowInfo | undefined): boolean {
  return row !== undefined && !row.keyed;
}

/** Exact comparison without serializing the engine's bigint cell values. */
export function sameCells(left: RowInfo["cells"], right: RowInfo["cells"]): boolean {
  return left === right || (left.length === right.length && left.every((cell, index) => {
    const other = right[index];
    return cell.key === other.key && sameCellValue(cell.value, other.value);
  }));
}

function RegisterInput({ name, value, disabled, onApply }: {
  name: string;
  value: bigint;
  disabled: boolean;
  onApply: (value: bigint) => void;
}) {
  const [draft, setDraft] = useState(value.toString());
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setDraft(value.toString()); setError(null); }, [value]);
  const apply = () => {
    const trimmed = draft.trim();
    if (!/^-?\d+$/.test(trimmed)) {
      setError("Enter a whole number.");
      return;
    }
    onApply(BigInt(trimmed));
    setError(null);
  };
  return (
    <Group gap={6} wrap="nowrap" align="flex-start">
      <TextInput
        aria-label={"Preview value for " + name}
        size="xs"
        className={classes.registerInput}
        classNames={{ input: classes.mono }}
        value={draft}
        disabled={disabled}
        onChange={e => setDraft(e.currentTarget.value)}
        error={error}
        onKeyDown={e => {
          if (e.key === "Enter") apply();
          if (e.key === "Escape") {
            setDraft(value.toString());
            setError(null);
          }
        }}
      />
      <Button
        size="xs"
        variant="default"
        aria-label={"Apply preview value for " + name}
        disabled={disabled || draft === value.toString()}
        onClick={apply}
      >
        Apply
      </Button>
    </Group>
  );
}

/**
 * The rows table: every `state.world[]` row of the composed document (name, kind, keyed, cell count, min/max),
 * and — while a preview session is running — the LIVE values from `useStudioPreview().rows`
 * (`RowInfo[]`, straight off the engine — no "mask" name heuristic and no 64-bit range check of
 * this component's own: the engine itself refuses an out-of-range write). A row that changed
 * between the previous and current preview snapshot is highlighted; a scalar (non-keyed) row's
 * numeric value is editable in place through `PREVIEW_WRITE`, and any other live row is marked
 * read-only.
 */
export function StateMatrixView() {
  const composition = useStudioComposition();
  const preview = useStudioPreview();
  const actor = StudioContext.useActorRef();
  const canWrite = useStudioCanDrivePreview();

  const authoredRows = listStateRows(composition);
  const liveByName = new Map(preview.rows.map(row => [row.name, row]));
  const previous = preview.snapshots[preview.cursor - 1];
  const previousByName = previous ? new Map(previous.rows.map(row => [row.name, row])) : null;
  const previewActive = preview.status === "ready";

  const changed = new Set<string>();
  if (previousByName) {
    for (const row of preview.rows) {
      const before = previousByName.get(row.name);
      if (!before || !sameCells(before.cells, row.cells)) {
        changed.add(row.name);
      }
    }
  }

  const boundRows = authoredRows.filter((row: WorldStateRow) => !row.domain || row.domain.$type === "slot");
  const cellsOfRows = authoredRows
    .filter(row => row.domain?.$type === "cellsOf")
    .map(row => row.name + " on " + (row.domain as CellsOfDomain).topology);

  return (
    <Paper p="md">
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start" gap="sm">
          <Stack gap={2}>
            <Kicker>{previewActive ? `Preview session · tick ${preview.tick.toString()}` : "Composed state"}</Kicker>
            <Title order={2} size="h4">Preview registers</Title>
          </Stack>
          <Button
            size="xs"
            variant="subtle"
            leftSection={<RiRestartLine size={16} />}
            disabled={!canWrite}
            onClick={() => actor.send({ type: "RESET_WORLD" })}
          >
            Reset preview
          </Button>
        </Group>
        <Text size="sm" c="dimmed">
          Apply a value to the preview session, then use Tick to evaluate rules. These edits do not change the source.
        </Text>

        {boundRows.length === 0
          ? (
            <EmptyState icon={<RiTableLine size={22} />} title="No world state rows">
              The composed document has no <Code>state.world</Code> rows to inspect yet.
            </EmptyState>
          )
          : (
            <Well>
              <ScrollArea.Autosize mah={420}>
                <Table stickyHeader>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Register</Table.Th>
                      <Table.Th>Kind</Table.Th>
                      <Table.Th>Value</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {boundRows.map(row => {
                      const live = liveByName.get(row.name);
                      const scalar = isScalarLive(live);
                      const carried = live?.cells[0]?.value ?? null;
                      // Only a numeric carrier is editable in place: a text, bool, or vector row has no whole-number
                      // operand the register input takes.
                      const value = cellNumber(carried);
                      const editable = previewActive && scalar && value !== null;
                      const isChanged = changed.has(row.name);
                      return (
                        <Table.Tr key={row.name} className={isChanged ? classes.changed : undefined}>
                          <Table.Td>
                            <Group gap="xs" wrap="nowrap">
                              <Text size="sm" ff="monospace" className={classes.name}>{row.name}</Text>
                              {isChanged && <Badge size="xs" color="jade">changed</Badge>}
                            </Group>
                          </Table.Td>
                          <Table.Td><Text size="xs" c="dimmed" ff="monospace">{row.kind}</Text></Table.Td>
                          <Table.Td>
                            {editable
                              ? (
                                <RegisterInput
                                  name={row.name}
                                  value={value}
                                  disabled={!canWrite}
                                  onApply={next => actor.send({ type: "PREVIEW_WRITE", row: row.name, value: next, write: "set" })}
                                />
                              )
                              : (
                                <Group gap={6} wrap="nowrap">
                                  <Text size="sm" ff="monospace" c={previewActive ? "dimmed" : undefined}>
                                    {live ? (scalar ? (cellText(carried) ?? "—") : live.cells.length + " cells") : (row.value ?? row.min ?? "—")}
                                  </Text>
                                  {previewActive && live && (
                                    <Badge size="xs" color="gray" leftSection={<RiLockLine size={10} />}>read-only</Badge>
                                  )}
                                </Group>
                              )}
                          </Table.Td>
                        </Table.Tr>
                      );
                    })}
                  </Table.Tbody>
                </Table>
              </ScrollArea.Autosize>
            </Well>
          )}

        {cellsOfRows.length > 0 && (
          <Text size="xs" c="dimmed">{cellsOfRows.join(" · ")}</Text>
        )}
      </Stack>
    </Paper>
  );
}

export default StateMatrixView;
