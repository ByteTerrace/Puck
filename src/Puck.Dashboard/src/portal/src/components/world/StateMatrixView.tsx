import React, { useEffect, useState } from "react";
import { Button, Card, Group, ScrollArea, Stack, Table, Text, TextInput } from "@mantine/core";
import { StateDelta } from "../../engine/tickRunner";
import { StateRowDefinition } from "../../authoring/documentTools";
export interface StateMatrixViewProps {
  stateDefinitions: StateRowDefinition[];
  currentState: Record<string, any>;
  lastDeltas?: StateDelta[];
  onStateChange: (stateName: string, newValue: any) => void;
  onResetState?: () => void;
  onHoverMask?: (mask: bigint | null) => void;
}
function RegisterInput({ row, value, onChange }: {
  row: StateRowDefinition;
  value: any;
  onChange: (value: any) => void;
}) {
  const [draft, setDraft] = useState(String(value));
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setDraft(String(value)); setError(null); }, [value]);
  const apply = () => {
    try {
      let number: bigint;
      if(row.kind === "bool") {
        if(!["true", "false", "0", "1"].includes(draft))
          throw new Error("Use true, false, 0 or 1.");
        number = draft === "true" || draft === "1" ? 1n : 0n;
      }
      else {
        if(!/^-?\d+$/.test(draft))
          throw new Error("Enter a whole number.");
        number = BigInt(draft);
      }
      if(number < -(1n << 63n) || number >= (1n << 64n))
        throw new Error("Value exceeds preview's 64-bit range.");
      if((row.nonNegative && number < 0n) || (row.min !== undefined && number < BigInt(row.min)) || (row.max !== undefined && number > BigInt(row.max)))
        throw new Error("Value is outside declared bounds.");
      onChange(row.kind === "bool" ? number !== 0n : number >= BigInt(Number.MIN_SAFE_INTEGER) && number <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(number) : number);
      setError(null);
    }
    catch(e) {
      setError((e as Error).message);
    }
  };
  return <Group
    gap={6}
    wrap="nowrap"
    align="flex-start"><TextInput
      aria-label={"Preview value for " + row.name}
      value={draft}
      onChange={e => setDraft(e.currentTarget.value)}
      error={error}
      onKeyDown={e => {
        if(e.key === "Enter")
          apply(); if(e.key === "Escape") {
            setDraft(String(value));
            setError(null);
          }
      }}
      styles={{ input: { fontFamily: "ui-monospace,monospace" } }}
      style={{ minWidth: 100, flex: 1 }} /><Button
        variant="default"
        aria-label={"Apply preview value for " + row.name}
        disabled={draft === String(value)}
        onClick={apply}>Apply</Button></Group>;
}
export const StateMatrixView: React.FC<StateMatrixViewProps> = ({ stateDefinitions, currentState, lastDeltas = [], onStateChange, onResetState, onHoverMask }) => {
  const changed = new Set(lastDeltas.map(d => d.target));
  const [mask, setMask] = useState<string | null>(null);
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
            onClick={onResetState}>Reset preview</Button></Group>
      <Text
        size="sm"
        c="var(--ink-soft)">Apply a value to advance one preview tick. These edits do not change the authored document.</Text>
      <ScrollArea
        h={350}
        offsetScrollbars><Table
          verticalSpacing="sm"><Table.Thead><Table.Tr><Table.Th>Register</Table.Th><Table.Th>Value</Table.Th></Table.Tr></Table.Thead><Table.Tbody>
            {stateDefinitions.filter(row => !row.domain).map(row => <Table.Tr
              key={row.name}
              style={{ background: changed.has(row.name) ? "var(--quote-bg)" : undefined }}><Table.Td><Text
                size="sm"
                ff="monospace">{row.name}</Text><Text
                  size="xs"
                  c="var(--ink-soft)">{row.kind ?? "int"}{changed.has(row.name) ? " · changed" : ""}</Text>
                {row.name.toLowerCase().includes("mask") && <Button
                  size="compact-sm"
                  variant="subtle"
                  aria-pressed={mask === row.name}
                  onClick={() => {
                    const next = mask === row.name ? null : row.name; setMask(next); try {
                      onHoverMask?.(next ? BigInt(currentState[row.name] ?? 0) : null);
                    }
                      catch {
                        onHoverMask?.(null);
                      }
                  }}>Highlight mask</Button>}
              </Table.Td><Table.Td><RegisterInput
                row={row}
                value={currentState[row.name] ?? row.value ?? 0}
                onChange={value => onStateChange(row.name, value)} /></Table.Td></Table.Tr>)}
          </Table.Tbody></Table></ScrollArea>
      <Text
        size="xs"
        c="var(--ink-soft)">{stateDefinitions.filter(row => row.domain).map(row => row.name + " on " + row.domain?.topology).join(" · ")}</Text>
    </Stack></Card>;
};
export default React.memo(StateMatrixView);
