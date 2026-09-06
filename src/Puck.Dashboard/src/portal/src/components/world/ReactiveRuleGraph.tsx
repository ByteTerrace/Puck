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

export interface ActionPredicate {
  $type: string;
  comparison?: string;
  state?: string;
  comparandState?: string;
  value?: number | string;
  key?: string;
  predicates?: ActionPredicate[];
  predicate?: ActionPredicate;
}

export interface ActionEffect {
  $type: string;
  state: string;
  fromState?: string;
  value?: number | string;
  expression?: string;
  key?: string;
}

export interface WorldRule {
  name: string;
  mode?: "Level" | "Edge";
  forEach?: string | null;
  gate?: ActionPredicate | null;
  effects?: ActionEffect[];
}

export interface ReactiveRuleGraphProps {
  rules: WorldRule[];
  currentState?: Record<string, any>;
  onSelectRule?: (ruleName: string) => void;
}

export const ReactiveRuleGraph: React.FC<ReactiveRuleGraphProps> = ({
  rules,
  currentState = {},
  onSelectRule,
}) => {
  // Evaluates a predicate against the current state frame
  const evaluatePredicate = (pred: ActionPredicate): boolean => {
    if (!pred) return true;

    if (pred.$type === "all") {
      return (pred.predicates ?? []).every((p) => evaluatePredicate(p));
    }
    if (pred.$type === "any") {
      return (pred.predicates ?? []).some((p) => evaluatePredicate(p));
    }
    if (pred.$type === "not") {
      return pred.predicate ? !evaluatePredicate(pred.predicate) : true;
    }

    if (pred.$type === "compareState") {
      const leftVal = currentState[pred.state ?? ""] ?? 0;
      const rightVal =
        pred.comparandState !== undefined
          ? currentState[pred.comparandState] ?? 0
          : pred.value ?? 0;

      switch (pred.comparison) {
        case "Equal":
          return leftVal == rightVal;
        case "NotEqual":
          return leftVal != rightVal;
        case "Greater":
          return leftVal > rightVal;
        case "GreaterOrEqual":
          return leftVal >= rightVal;
        case "Less":
          return leftVal < rightVal;
        case "LessOrEqual":
          return leftVal <= rightVal;
        default:
          return true;
      }
    }

    return true;
  };

  // Render a recursive gate predicate tree
  const renderPredicateTree = (pred: ActionPredicate, depth = 0): React.ReactNode => {
    if (!pred) return <Text size="xs" c="dimmed">Always (No Gate)</Text>;

    const isPass = evaluatePredicate(pred);

    if (pred.$type === "all" || pred.$type === "any") {
      const isAll = pred.$type === "all";
      return (
        <Stack gap={4} ml={depth * 14} style={{ borderLeft: "2px solid var(--mantine-color-dark-4)", paddingLeft: 8 }}>
          <Group gap={6}>
            <Badge size="xs" variant="filled" color={isAll ? "indigo" : "orange"}>
              {isAll ? "ALL (AND)" : "ANY (OR)"}
            </Badge>
            <ThemeIcon size={16} radius="xl" color={isPass ? "teal" : "red"}>
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
        <Stack gap={4} ml={depth * 14} style={{ borderLeft: "2px solid var(--mantine-color-dark-4)", paddingLeft: 8 }}>
          <Badge size="xs" variant="filled" color="pink">
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
          background: "var(--mantine-color-dark-7)",
          border: `1px solid ${isPass ? "var(--mantine-color-teal-8)" : "var(--mantine-color-red-9)"}`,
        }}
      >
        <Group gap={6} wrap="nowrap">
          <ThemeIcon size={14} radius="xl" color={isPass ? "teal" : "red"}>
            {isPass ? <RiCheckboxCircleFill size={10} /> : <RiCloseCircleFill size={10} />}
          </ThemeIcon>
          <Text size="xs" ff="monospace">
            {pred.state} {pred.comparison} {pred.comparandState ?? String(pred.value)}
            {pred.key ? ` [${pred.key}]` : ""}
          </Text>
        </Group>
      </Paper>
    );
  };

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--mantine-color-dark-8)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiGitMergeLine size={18} color="#a855f7" />
          <Text fw={700} size="sm">
            Reactive Rule Network
          </Text>
          <Badge variant="outline" color="grape" size="xs">
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
                      <ThemeIcon size={18} radius="xl" color={gatePassed ? "teal" : "gray"}>
                        {gatePassed ? <RiFlashlightFill size={12} /> : <RiCloseCircleFill size={12} />}
                      </ThemeIcon>
                      <Text fw={600} size="xs" ff="monospace">
                        {rule.name}
                      </Text>
                    </Group>
                    <Group gap={4}>
                      {rule.mode && (
                        <Badge size="xs" variant="light" color={rule.mode === "Edge" ? "cyan" : "violet"}>
                          {rule.mode}
                        </Badge>
                      )}
                      {rule.forEach && (
                        <Badge size="xs" variant="outline" color="yellow">
                          for: {rule.forEach}
                        </Badge>
                      )}
                      <Badge size="xs" color={gatePassed ? "teal" : "gray"} variant="filled">
                        {gatePassed ? "FIRING" : "QUIET"}
                      </Badge>
                    </Group>
                  </Group>
                </Accordion.Control>

                <Accordion.Panel>
                  <Stack gap="xs">
                    {/* Gate Evaluation Hierarchy */}
                    <Box>
                      <Text size="xs" fw={700} c="dimmed" mb={4}>
                        GATE LOGIC TREE:
                      </Text>
                      {rule.gate ? renderPredicateTree(rule.gate) : <Text size="xs" c="dimmed">Unconditional</Text>}
                    </Box>

                    {/* Effects Pipeline */}
                    <Box>
                      <Text size="xs" fw={700} c="dimmed" mb={4}>
                        EFFECT SEQUENCE ({(rule.effects ?? []).length}):
                      </Text>
                      <Stack gap={4}>
                        {(rule.effects ?? []).map((eff, i) => (
                          <Paper
                            key={i}
                            p={4}
                            radius="xs"
                            style={{ background: "var(--mantine-color-dark-6)", borderLeft: "3px solid var(--mantine-color-blue-5)" }}
                          >
                            <Group gap={6} wrap="nowrap">
                              <RiArrowRightLine size={12} color="#38bdf8" />
                              <Badge size="xs" variant="outline" color="blue">
                                {eff.$type}
                              </Badge>
                              <Text size="xs" ff="monospace">
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
