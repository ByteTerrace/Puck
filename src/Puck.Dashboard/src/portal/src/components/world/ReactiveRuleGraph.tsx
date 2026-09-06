import React from "react";
import {
  Box,
  Card,
  Group,
  Stack,
  Text,
  Badge,
  Accordion,
  ThemeIcon,
  Paper,
  ScrollArea,
} from "@mantine/core";
import {
  RiCheckboxCircleFill,
  RiCloseCircleFill,
  RiFlashlightFill,
  RiGitMergeLine,
  RiArrowRightLine,
} from "@remixicon/react";
import {
  ActionPredicate,
  ActionEffect,
  WorldRule,
  evaluatePuckPredicate,
} from "../../engine/evaluator";

export type { ActionPredicate, ActionEffect, WorldRule };

export interface ReactiveRuleGraphProps {
  rules: WorldRule[];
  currentState?: Record<string, any>;
  boardCells?: Record<string,Record<number,number>>;
  onSelectRule?: (ruleName: string) => void;
}

export const ReactiveRuleGraph: React.FC<ReactiveRuleGraphProps> = ({
  rules,
  currentState = {},
  boardCells = {},
  onSelectRule,
}) => {
  const evaluatePredicate = (pred: ActionPredicate): boolean => {
    try { return evaluatePuckPredicate(pred,currentState,boardCells).passed; } catch { return false; }
  };

  // Render a recursive gate predicate tree
  const renderPredicateTree = (pred: ActionPredicate, depth = 0): React.ReactNode => {
    if (!pred) return <Text size="xs" c="var(--ink-faint)">Always (No Gate)</Text>;

    let isPass: boolean;
    try { isPass = evaluatePuckPredicate(pred,currentState,boardCells).passed; } catch (error) { return <Text size="xs">Preview unavailable: {(error as Error).message}</Text>; }

    if (pred.$type === "all" || pred.$type === "any") {
      const isAll = pred.$type === "all";
      return (
        <Stack gap={4} ml={depth * 14} style={{ borderLeft: "2px solid var(--rule)", paddingLeft: 8 }}>
          <Group gap={6}>
            <Badge size="xs" variant="filled" color={isAll ? "coral" : "jade"}>
              {isAll ? "ALL (AND)" : "ANY (OR)"}
            </Badge>
            <ThemeIcon size={16} radius="xl" color={isPass ? "jade" : "coral"}>
              {isPass ? <RiCheckboxCircleFill size={12} /> : <RiCloseCircleFill size={12} />}
            </ThemeIcon>
          </Group>
          {(pred.predicates ?? []).map((child, idx) => (
            <React.Fragment key={idx}>{renderPredicateTree(child, depth + 1)}</React.Fragment>
          ))}
        </Stack>
      );
    }

    if (pred.$type === "not" && pred.predicate) {
      return (
        <Stack gap={4} ml={depth * 14} style={{ borderLeft: "2px solid var(--rule)", paddingLeft: 8 }}>
          <Badge size="xs" variant="filled" color="coral">
            NOT
          </Badge>
          {renderPredicateTree(pred.predicate, depth + 1)}
        </Stack>
      );
    }

    // Leaf comparison predicate
    return (
      <Paper
        p={4}
        radius="xs"
        ml={depth * 14}
        style={{
          background: "var(--quote-bg)",
          border: `1px solid ${isPass ? "var(--accent-2)" : "var(--accent)"}`,
        }}
      >
        <Group gap={6} wrap="nowrap">
          <ThemeIcon size={14} radius="xl" color={isPass ? "jade" : "coral"}>
            {isPass ? <RiCheckboxCircleFill size={10} /> : <RiCloseCircleFill size={10} />}
          </ThemeIcon>
          <Text size="xs" ff="monospace" style={{ color: "var(--ink)" }}>
            {pred.state} {pred.comparison} {pred.comparandState ?? String(pred.value)}
            {pred.key ? ` [${pred.key}]` : ""}
          </Text>
        </Group>
      </Paper>
    );
  };

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiGitMergeLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Reactive Rule Network
          </Text>
          <Badge variant="light" color="coral" size="xs">
            {rules.length} Rules
          </Badge>
        </Group>
      </Group>

      <ScrollArea h={420} offsetScrollbars>
        <Accordion variant="separated" radius="sm">
          {rules.map((rule) => {
            const gatePassed = rule.gate ? evaluatePredicate(rule.gate) : true;

            return (
              <Accordion.Item key={rule.name} value={rule.name} onClick={() => onSelectRule?.(rule.name)}>
                <Accordion.Control>
                  <Group justify="space-between" wrap="nowrap" pr="xs">
                    <Group gap="xs" wrap="nowrap">
                      <ThemeIcon size={18} radius="xl" color={gatePassed ? "jade" : "gray"}>
                        {gatePassed ? <RiFlashlightFill size={12} /> : <RiCloseCircleFill size={12} />}
                      </ThemeIcon>
                      <Text fw={600} size="xs" ff="monospace" style={{ color: "var(--ink)" }}>
                        {rule.name}
                      </Text>
                    </Group>
                    <Group gap={4}>
                      {rule.mode && (
                        <Badge size="xs" variant="light" color={rule.mode === "Edge" ? "jade" : "coral"}>
                          {rule.mode}
                        </Badge>
                      )}
                      {rule.forEach && (
                        <Badge size="xs" variant="outline" color="gray">
                          for: {rule.forEach}
                        </Badge>
                      )}
                      <Badge size="xs" color={gatePassed ? "jade" : "gray"} variant={gatePassed ? "filled" : "outline"}>
                        {gatePassed ? "FIRING" : "QUIET"}
                      </Badge>
                    </Group>
                  </Group>
                </Accordion.Control>

                <Accordion.Panel>
                  <Stack gap="xs">
                    {/* Gate Evaluation Hierarchy */}
                    <Box>
                      <Text size="xs" fw={700} c="var(--ink-faint)" mb={4} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                        GATE LOGIC TREE:
                      </Text>
                      {rule.gate ? renderPredicateTree(rule.gate) : <Text size="xs" c="var(--ink-faint)">Unconditional</Text>}
                    </Box>

                    {/* Effects Pipeline */}
                    <Box>
                      <Text size="xs" fw={700} c="var(--ink-faint)" mb={4} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                        EFFECT SEQUENCE ({(rule.effects ?? []).length}):
                      </Text>
                      <Stack gap={4}>
                        {(rule.effects ?? []).map((eff, i) => (
                          <Paper
                            key={i}
                            p={4}
                            radius="xs"
                            style={{ background: "var(--code-bg)", borderLeft: "3px solid var(--accent-2)" }}
                          >
                            <Group gap={6} wrap="nowrap">
                              <RiArrowRightLine size={12} color="var(--accent-2)" />
                              <Badge size="xs" variant="outline" color="jade">
                                {eff.$type}
                              </Badge>
                              <Text size="xs" ff="monospace" style={{ color: "var(--code-fg)" }}>
                                {eff.state}
                                {eff.fromState ? ` = ${eff.fromState}` : ""}
                                {eff.value !== undefined ? ` = ${eff.value}` : ""}
                                {eff.expression ? ` = ${eff.expression}` : ""}
                                {eff.key ? ` [key: ${eff.key}]` : ""}
                              </Text>
                            </Group>
                          </Paper>
                        ))}
                      </Stack>
                    </Box>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            );
          })}
        </Accordion>
      </ScrollArea>
    </Card>
  );
};

export default ReactiveRuleGraph;
