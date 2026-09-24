import { Alert, Badge, Group, ScrollArea, Stack, Table, Text, UnstyledButton, VisuallyHidden } from "@mantine/core";
import { RiErrorWarningLine, RiInformationLine } from "@remixicon/react";
import type { JudgeTrace, SourceSpan } from "../../native/engineTypes";
import { Kicker } from "../../ui/Kicker";
import classes from "./RuleTraceView.module.css";
import { Well } from "../../ui/Well";

/** A rule's name, as a link to its source when the source map resolves it; the same text either way. */
function RuleName({ name, span, onReveal }: { readonly name: string; readonly span: SourceSpan | null; readonly onReveal?: (span: SourceSpan) => void }) {
  if (!span || !onReveal) return <Text size="sm" ff="monospace" className={classes.name}>{name}</Text>;
  return (
    <UnstyledButton className={`${classes.name} ${classes.link}`} title={`${span.path}:${span.line}:${span.column}`} onClick={() => onReveal(span)}>
      {name}
    </UnstyledButton>
  );
}

/**
 * Renders one preview snapshot's `JudgeTrace` (`engine.judge`'s own answer): every rule the
 * `judge` call visited, in order, with its `mode`; the writes it produced (row/key/old → new,
 * every value a `bigint` printed as a decimal string — never a JavaScript `number`); and any
 * refusals it recorded. `evaluations` is deliberately left opaque (see `JudgeTrace`'s own
 * remarks) — this view names the rule and its mode, not each evaluation's shape. A rule `locate` finds in source
 * is a link to that span.
 */
export function RuleTraceView({ trace, locate, onReveal }: {
  readonly trace: JudgeTrace | null;
  readonly locate?: (rule: string) => SourceSpan | null;
  readonly onReveal?: (span: SourceSpan) => void;
}) {
  if (!trace) {
    return <Text size="sm" c="dimmed">No trace yet — tick the preview to produce one.</Text>;
  }

  return (
    <Stack gap="md">
      {trace.refusals.length > 0 && (
        <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Refusals">
          <Stack gap={4}>
            {trace.refusals.map((refusal, index) => {
              const span = locate?.(refusal.rule) ?? null;
              return (
                <Text key={index} size="sm" c="red">
                  {refusal.text}
                  {span && onReveal && (
                    <>
                      {" "}
                      <UnstyledButton className={classes.link} onClick={() => onReveal(span)}>
                        {span.path}:{span.line}
                      </UnstyledButton>
                    </>
                  )}
                </Text>
              );
            })}
          </Stack>
        </Alert>
      )}

      <Stack gap={6}>
        <Group justify="space-between" gap="xs">
          <Kicker>Rules visited</Kicker>
          <Text size="xs" c="dimmed" ff="monospace">{trace.rules.length}</Text>
        </Group>
        {trace.rules.length === 0
          ? <Text size="xs" c="dimmed">No rules ran this tick.</Text>
          : (
            <Well>
              <ScrollArea.Autosize mah={240}>
                <ol className={classes.log}>
                  {trace.rules.map((rule, index) => (
                    <li key={`${rule.name}-${index}`} className={classes.entry}>
                      <Text size="xs" c="dimmed" ff="monospace" className={classes.ordinal}>{index + 1}</Text>
                      <Badge size="xs" color="gray">{rule.mode}</Badge>
                      <RuleName name={rule.name} span={locate?.(rule.name) ?? null} onReveal={onReveal} />
                      <Text size="xs" c="dimmed" className={classes.count}>
                        {rule.evaluations.length} evaluation{rule.evaluations.length === 1 ? "" : "s"}
                      </Text>
                    </li>
                  ))}
                </ol>
              </ScrollArea.Autosize>
            </Well>
          )}
      </Stack>

      <Stack gap={6}>
        <Group justify="space-between" gap="xs">
          <Kicker>Writes</Kicker>
          <Text size="xs" c="dimmed" ff="monospace">{trace.writes.length}</Text>
        </Group>
        {trace.writes.length === 0
          ? <Text size="xs" c="dimmed">No row was written this tick.</Text>
          : (
            <Well>
              <ScrollArea.Autosize mah={240}>
                <Table fz="xs" verticalSpacing={4} stickyHeader>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Row</Table.Th>
                      <Table.Th>Key</Table.Th>
                      <Table.Th>Old → New</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {trace.writes.map((write, index) => (
                      <Table.Tr key={index}>
                        <Table.Td ff="monospace">{write.row}</Table.Td>
                        <Table.Td ff="monospace" c={write.key === null ? "dimmed" : undefined}>{write.key ?? "—"}</Table.Td>
                        <Table.Td ff="monospace">
                          <Text span inherit c="dimmed">{write.old.toString()}</Text>
                          <span className={classes.arrow} aria-hidden>→</span><VisuallyHidden>to</VisuallyHidden>
                          <Text span inherit>{write.new.toString()}</Text>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </ScrollArea.Autosize>
            </Well>
          )}
      </Stack>

      {trace.hostFacts.length > 0 && (
        <Alert color="yellow" icon={<RiInformationLine size={18} />} title="Host facts this engine cannot supply">
          <Stack gap={2}>
            {trace.hostFacts.map((fact, index) => (
              <Text key={index} size="xs" ff="monospace">{fact.rule}: {fact.operand} read as {fact.answer}</Text>
            ))}
          </Stack>
        </Alert>
      )}
    </Stack>
  );
}

export default RuleTraceView;
