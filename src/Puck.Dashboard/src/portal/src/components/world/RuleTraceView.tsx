import { Badge, Group, ScrollArea, Stack, Table, Text } from "@mantine/core";
import type { JudgeTrace } from "../../native/engineTypes";

/**
 * Renders one preview snapshot's `JudgeTrace` (`engine.judge`'s own answer): every rule the
 * `judge` call visited, in order, grouped by `mode`; the writes it produced (row/key/old/new,
 * every value a `bigint` printed as a decimal string — never a JavaScript `number`); and any
 * refusals it recorded. `evaluations` is deliberately left opaque (see `JudgeTrace`'s own
 * remarks) — this view names the rule and its mode, not each evaluation's shape.
 */
export function RuleTraceView({ trace }: { readonly trace: JudgeTrace | null }) {
  if (!trace) {
    return <Text size="sm" c="dimmed">No trace yet — tick the preview to produce one.</Text>;
  }

  return (
    <Stack gap="md">
      <div>
        <Text fw={600} size="sm" mb={4}>Rules visited</Text>
        {trace.rules.length === 0 && <Text size="xs" c="dimmed">No rules ran this tick.</Text>}
        <ScrollArea.Autosize mah={220}>
          <Stack gap={4}>
            {trace.rules.map((rule, index) => (
              <Group key={`${rule.name}-${index}`} gap="xs" wrap="nowrap">
                <Badge size="xs" variant="light" color="jade">{rule.mode}</Badge>
                <Text size="sm" ff="monospace">{rule.name}</Text>
                <Text size="xs" c="dimmed">{rule.evaluations.length} evaluation{rule.evaluations.length === 1 ? "" : "s"}</Text>
              </Group>
            ))}
          </Stack>
        </ScrollArea.Autosize>
      </div>

      <div>
        <Text fw={600} size="sm" mb={4}>Writes</Text>
        {trace.writes.length === 0 && <Text size="xs" c="dimmed">No row was written this tick.</Text>}
        {trace.writes.length > 0 && (
          <ScrollArea.Autosize mah={220}>
            <Table verticalSpacing={4} fz="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Row</Table.Th>
                  <Table.Th>Key</Table.Th>
                  <Table.Th>Old</Table.Th>
                  <Table.Th>New</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {trace.writes.map((write, index) => (
                  <Table.Tr key={index}>
                    <Table.Td ff="monospace">{write.row}</Table.Td>
                    <Table.Td ff="monospace">{write.key ?? "—"}</Table.Td>
                    <Table.Td ff="monospace">{write.old.toString()}</Table.Td>
                    <Table.Td ff="monospace">{write.new.toString()}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </ScrollArea.Autosize>
        )}
      </div>

      {trace.hostFacts.length > 0 && (
        <div>
          <Text fw={600} size="sm" mb={4} c="yellow">Host facts this engine cannot supply</Text>
          <Stack gap={2}>
            {trace.hostFacts.map((fact, index) => (
              <Text key={index} size="xs" c="dimmed">{fact.rule}: {fact.operand} read as {fact.answer}</Text>
            ))}
          </Stack>
        </div>
      )}

      {trace.refusals.length > 0 && (
        <div>
          <Text fw={600} size="sm" mb={4} c="red">Refusals</Text>
          <Stack gap={4}>
            {trace.refusals.map((refusal, index) => (
              <Text key={index} size="xs" c="red" role="alert">{refusal}</Text>
            ))}
          </Stack>
        </div>
      )}
    </Stack>
  );
}

export default RuleTraceView;
