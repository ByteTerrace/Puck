import React, { useEffect, useMemo, useState } from "react";
import { Button, Card, Group, ScrollArea, Stack, Table, Text, TextInput } from "@mantine/core";
import { StudioContext, useStudioDocument, useStudioPreview } from "../../context/StudioContext";
import { listStateRows, type CellsOfDomain, type WorldStateRow } from "../../authoring/documentTools";
import type { RowInfo } from "../../native/engineTypes";

function isScalarLive(row: RowInfo | undefined): boolean {
  return row !== undefined && !row.keyed;
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
  return <Group
    gap={6}
    wrap="nowrap"
    align="flex-start"><TextInput
      aria-label={"Preview value for " + name}
      value={draft}
      disabled={disabled}
      onChange={e => setDraft(e.currentTarget.value)}
      error={error}
      onKeyDown={e => {
        if(e.key === "Enter")
          apply(); if(e.key === "Escape") {
            setDraft(value.toString());
            setError(null);
          }
      }}
      styles={{ input: { fontFamily: "ui-monospace,monospace" } }}
      style={{ minWidth: 100, flex: 1 }} /><Button
        variant="default"
        aria-label={"Apply preview value for " + name}
        disabled={disabled || draft === value.toString()}
        onClick={apply}>Apply</Button></Group>;
}

/**
 * The rows table: every authored `state.world[]` row (name, kind, keyed, cell count, min/max),
 * and — while a preview session is running — the LIVE values from `useStudioPreview().rows`
 * (`RowInfo[]`, straight off the engine — no "mask" name heuristic and no 64-bit range check of
 * this component's own: the engine itself refuses an out-of-range write). A row that changed
 * between the previous and current preview snapshot is highlighted; a scalar (non-keyed) row's
 * value is editable in place through `PREVIEW_WRITE`.
 */
export const StateMatrixView: React.FC = () => {
  const document = useStudioDocument();
  const preview = useStudioPreview();
  const actor = StudioContext.useActorRef();

  const authoredRows = useMemo(() => listStateRows(document.value), [document.value]);
  const liveByName = useMemo(() => new Map(preview.rows.map(row => [row.name, row])), [preview.rows]);
  const previousByName = useMemo(() => {
    const previous = preview.snapshots[preview.cursor - 1];
    return previous ? new Map(previous.rows.map(row => [row.name, row])) : null;
  }, [preview.snapshots, preview.cursor]);

  const previewActive = preview.status === "ready";

  const changed = useMemo(() => {
    if (!previousByName) return new Set<string>();
    const names = new Set<string>();
    for (const row of preview.rows) {
      const before = previousByName.get(row.name);
      if (!before || JSON.stringify(before.cells) !== JSON.stringify(row.cells)) {
        names.add(row.name);
      }
    }
    return names;
  }, [preview.rows, previousByName]);

  const boundRows = authoredRows.filter((row: WorldStateRow) => !row.domain || row.domain.$type === "slot");

  return <Card
    withBorder
    radius="md"
    p="md"><Stack
      gap="sm">
      <Group
        justify="space-between"><Text
          component="h2"
          size="md"
          fw={650}>Preview registers</Text><Button
            variant="subtle"
            disabled={!previewActive}
            onClick={() => actor.send({ type: "RESET_WORLD" })}>Reset preview</Button></Group>
      <Text
        size="sm"
        c="var(--ink-soft)">Apply a value to advance one preview tick. These edits do not change the authored document.</Text>
      <ScrollArea
        h={350}
        offsetScrollbars><Table
          verticalSpacing="sm"><Table.Thead><Table.Tr><Table.Th>Register</Table.Th><Table.Th>Kind</Table.Th><Table.Th>Value</Table.Th></Table.Tr></Table.Thead><Table.Tbody>
            {boundRows.map(row => {
              const live = liveByName.get(row.name);
              const scalar = isScalarLive(live);
              const value = live?.cells[0]?.value ?? 0n;
              return <Table.Tr
                key={row.name}
                style={{ background: changed.has(row.name) ? "var(--quote-bg)" : undefined }}><Table.Td><Text
                  size="sm"
                  ff="monospace">{row.name}</Text>{changed.has(row.name) && <Text
                    size="xs"
                    c="var(--ink-soft)">changed</Text>}
              </Table.Td><Table.Td><Text
                size="xs"
                c="var(--ink-soft)">{row.kind}</Text></Table.Td><Table.Td>{previewActive && scalar
                ? <RegisterInput
                  name={row.name}
                  value={value}
                  disabled={false}
                  onApply={next => actor.send({ type: "PREVIEW_WRITE", row: row.name, value: next, write: "set" })} />
                : <Text
                  size="sm"
                  ff="monospace">{live ? (scalar ? value.toString() : live.cells.length + " cells") : (row.value ?? row.min ?? "—")}</Text>}</Table.Td></Table.Tr>;
            })}
          </Table.Tbody></Table></ScrollArea>
      <Text
        size="xs"
        c="var(--ink-soft)">{authoredRows.filter(row => row.domain?.$type === "cellsOf").map(row => row.name + " on " + (row.domain as CellsOfDomain).topology).join(" · ")}</Text>
    </Stack></Card>;
};
export default React.memo(StateMatrixView);
