import React from "react";
import {
  Box,
  Card,
  Group,
  Stack,
  Text,
  Badge,
  Table,
  NumberInput,
  Paper,
  ScrollArea,
  ActionIcon,
  Tooltip,
} from "@mantine/core";
import { RiDatabaseLine, RiRestartLine, RiLightbulbLine } from "@remixicon/react";
import { StateDelta } from "../../engine/tickRunner";

export interface StateRowDefinition {
  name: string;
  kind?: string;
  value?: number | string | boolean | bigint;
  min?: number;
  max?: number;
  nonNegative?: boolean;
  domain?: { $type?: string; topology?: string };
}

export interface StateMatrixViewProps {
  stateDefinitions: StateRowDefinition[];
  currentState: Record<string, any>;
  lastDeltas?: StateDelta[];
  onStateChange: (stateName: string, newValue: any) => void;
  onResetState?: () => void;
  onHoverMask?: (mask: bigint | null) => void;
}

export const StateMatrixView: React.FC<StateMatrixViewProps> = ({
  stateDefinitions,
  currentState,
  lastDeltas = [],
  onStateChange,
  onResetState,
  onHoverMask,
}) => {
  // Separate scalar state rows from topological/board rows
  const scalarRows = stateDefinitions.filter((d) => !d.domain);
  const boardRows = stateDefinitions.filter((d) => Boolean(d.domain));

  // Set of recently mutated register names
  const deltaMap = new Map<string, StateDelta>();
  lastDeltas.forEach((d) => deltaMap.set(d.target, d));

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiDatabaseLine size={18} color="var(--accent-2)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Live State Registers
          </Text>
          <Badge variant="light" color="jade" size="xs">
            {stateDefinitions.length} Rows
          </Badge>
        </Group>

        {onResetState && (
          <Tooltip label="Reset to initial world values" withArrow>
            <ActionIcon variant="subtle" color="gray" size="sm" onClick={onResetState}>
              <RiRestartLine size={16} />
            </ActionIcon>
          </Tooltip>
        )}
      </Group>

      <ScrollArea h={380} offsetScrollbars>
        <Stack gap="sm">
          {/* Scalar Registers */}
          <Table verticalSpacing="xs" striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: "0.72rem", color: "var(--ink-faint)" }}>
                  Register
                </Table.Th>
                <Table.Th style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: "0.72rem", color: "var(--ink-faint)" }}>
                  Kind / Range
                </Table.Th>
                <Table.Th style={{ width: 150, fontFamily: '"JetBrains Mono", monospace', fontSize: "0.72rem", color: "var(--ink-faint)" }}>
                  Value / Sandbox
                </Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {scalarRows.map((row) => {
                const val = currentState[row.name] ?? row.value ?? 0;
                const min = row.nonNegative ? Math.max(0, row.min ?? 0) : row.min;
                const max = row.max;
                const delta = deltaMap.get(row.name);
                const isMask = row.name.toLowerCase().includes("mask");

                return (
                  <Table.Tr
                    key={row.name}
                    onMouseEnter={() => {
                      if (isMask && val) {
                        try {
                          onHoverMask?.(BigInt(val));
                        } catch {}
                      }
                    }}
                    onMouseLeave={() => {
                      if (isMask) onHoverMask?.(null);
                    }}
                    style={{
                      background: delta ? "var(--quote-bg)" : undefined,
                      transition: "background 0.2s ease",
                    }}
                  >
                    <Table.Td>
                      <Group gap={6} wrap="nowrap">
                        <Text size="xs" fw={600} ff="monospace" style={{ color: "var(--ink)" }}>
                          {row.name}
                        </Text>
                        {delta && (
                          <Badge size="xs" color="coral" variant="filled" style={{ fontSize: 9 }}>
                            NEW
                          </Badge>
                        )}
                        {isMask && (
                          <Tooltip label="Hover row to illuminate mask cells in 3D CAD" withArrow>
                            <ActionIcon size="xs" variant="subtle" color="indigo">
                              <RiLightbulbLine size={12} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        <Badge size="xs" variant="light" color="coral">
                          {row.kind ?? "int"}
                        </Badge>
                        {(min !== undefined || max !== undefined) && (
                          <Text size="xs" ff="monospace" style={{ color: "var(--ink-faint)" }}>
                            [{min ?? "-∞"} .. {max ?? "+∞"}]
                          </Text>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <NumberInput
                        size="xs"
                        value={Number(val)}
                        min={min}
                        max={max}
                        onChange={(newVal) => onStateChange(row.name, Number(newVal))}
                        styles={{
                          input: {
                            fontFamily: '"JetBrains Mono", monospace',
                            fontWeight: "bold",
                            color: delta ? "var(--accent)" : "var(--accent-2)",
                            background: "var(--code-bg)",
                            borderColor: delta ? "var(--accent)" : "var(--rule)",
                          },
                        }}
                      />
                    </Table.Td>
                  </Table.Tr>
                );
              })}
            </Table.Tbody>
          </Table>

          {/* Board & Topological Rows */}
          {boardRows.length > 0 && (
            <Box mt="xs">
              <Text size="xs" fw={700} mb={6} style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-faint)" }}>
                TOPOLOGICAL BOARDS (cellsOf):
              </Text>
              <Group gap="xs">
                {boardRows.map((b) => (
                  <Paper
                    key={b.name}
                    p="xs"
                    radius="sm"
                    style={{ background: "var(--quote-bg)", border: "1px solid var(--rule)" }}
                  >
                    <Text size="xs" fw={600} ff="monospace" style={{ color: "var(--ink)" }}>
                      {b.name}
                    </Text>
                    <Text size="xs" style={{ color: "var(--ink-soft)" }}>
                      over lattice: <Badge size="xs" color="jade" variant="light">{b.domain?.topology ?? "unknown"}</Badge>
                    </Text>
                  </Paper>
                ))}
              </Group>
            </Box>
          )}
        </Stack>
      </ScrollArea>
    </Card>
  );
};

export default StateMatrixView;
