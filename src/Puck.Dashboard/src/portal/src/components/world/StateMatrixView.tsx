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
import { RiDatabaseLine, RiRestartLine } from "@remixicon/react";

export interface StateRowDefinition {
  name: string;
  kind?: string;
  value?: number | string | boolean;
  min?: number;
  max?: number;
  nonNegative?: boolean;
  domain?: { $type?: string; topology?: string };
}

export interface StateMatrixViewProps {
  stateDefinitions: StateRowDefinition[];
  currentState: Record<string, any>;
  onStateChange: (stateName: string, newValue: any) => void;
  onResetState?: () => void;
}

export const StateMatrixView: React.FC<StateMatrixViewProps> = ({
  stateDefinitions,
  currentState,
  onStateChange,
  onResetState,
}) => {
  // Separate scalar state rows from topological/board rows
  const scalarRows = stateDefinitions.filter((d) => !d.domain);
  const boardRows = stateDefinitions.filter((d) => Boolean(d.domain));

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--mantine-color-dark-8)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiDatabaseLine size={18} color="#10b981" />
          <Text fw={700} size="sm">
            Live State Registers
          </Text>
          <Badge variant="outline" color="teal" size="xs">
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
                <Table.Th>Register</Table.Th>
                <Table.Th>Kind / Range</Table.Th>
                <Table.Th style={{ width: 140 }}>Value / Sandbox</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {scalarRows.map((row) => {
                const val = currentState[row.name] ?? row.value ?? 0;
                const min = row.nonNegative ? Math.max(0, row.min ?? 0) : row.min;
                const max = row.max;

                return (
                  <Table.Tr key={row.name}>
                    <Table.Td>
                      <Text size="xs" fw={600} ff="monospace">
                        {row.name}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        <Badge size="xs" variant="dot" color="blue">
                          {row.kind ?? "int"}
                        </Badge>
                        {(min !== undefined || max !== undefined) && (
                          <Text size="xs" c="dimmed" ff="monospace">
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
                            fontFamily: "monospace",
                            fontWeight: "bold",
                            color: "var(--mantine-color-teal-4)",
                            background: "var(--mantine-color-dark-7)",
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
              <Text size="xs" fw={700} c="dimmed" mb={6}>
                TOPOLOGICAL BOARDS (cellsOf):
              </Text>
              <Group gap="xs">
                {boardRows.map((b) => (
                  <Paper
                    key={b.name}
                    p="xs"
                    radius="sm"
                    style={{ background: "var(--mantine-color-dark-7)", border: "1px solid var(--mantine-color-dark-5)" }}
                  >
                    <Text size="xs" fw={600} ff="monospace">
                      {b.name}
                    </Text>
                    <Text size="xs" c="dimmed">
                      over lattice: <Badge size="xs" color="cyan">{b.domain?.topology ?? "unknown"}</Badge>
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
