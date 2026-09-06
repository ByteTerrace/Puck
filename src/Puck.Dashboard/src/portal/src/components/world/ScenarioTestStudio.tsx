import { usePreviewBatch } from "../../hooks/usePreviewBatch";
import React from "react";
import {
  Card,
  Group,
  Stack,
  Text,
  Badge,
  Button,
  Paper,
  Progress,
  ScrollArea,
  ActionIcon,
  Tooltip,
} from "@mantine/core";
import {
  RiPlayCircleLine,
  RiCheckFill,
  RiCloseFill,
  RiFlaskLine,
  RiRefreshLine,
  RiTimeLine,
} from "@remixicon/react";
import {
  WorldRule,
  TopologyDefinition,
} from "../../engine/evaluator";
import {
  ScenarioExecutionResult,
  STANDARD_WORLD_SCENARIOS,
  WorldTestScenario,
} from "../../engine/scenarioRunner";

export interface ScenarioTestStudioProps {
  worldId: string;
  initial?: {state:Record<string,any>;boardCells:Record<string,Record<number,number>>};
  rules: WorldRule[];
  topologies: Record<string, TopologyDefinition>;
  customScenarios?: WorldTestScenario[];
  onLoadScenarioState?: (state: Record<string, any>, boardCells: Record<string, Record<number, number>>) => void;
}

export const ScenarioTestStudio: React.FC<ScenarioTestStudioProps> = ({
  worldId,
  initial,
  rules,
  topologies,
  customScenarios = [],
  onLoadScenarioState: _onLoadScenarioState,
}) => {
  const standard = STANDARD_WORLD_SCENARIOS[worldId] ?? [];
  const scenarios = [...standard, ...customScenarios];

  const {run,stop,running:isRunning,error,result}=usePreviewBatch<Record<string,ScenarioExecutionResult>>();
  const results=result??{};
  const handleRunAll=()=>run({kind:"scenarios",scenarios,rules,topologies,initial});
  const handleRunSingle=(scenario:WorldTestScenario)=>run({kind:"scenarios",scenarios:[scenario],rules,topologies,initial});

  const total = scenarios.length;
  const executedCount = Object.keys(results).length;
  const passedCount = Object.values(results).filter((r) => r.passed).length;
  const failedCount = executedCount - passedCount;
  const passRate = executedCount > 0 ? Math.round((passedCount / executedCount) * 100) : 0;

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      {error&&<Text role="alert" c="red">{error}</Text>}
      {isRunning&&<Button onClick={stop} variant="default">Cancel checks</Button>}
      {/* Header */}
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiFlaskLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Offline scenario checks
          </Text>
          <Badge variant="light" color="coral" size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
            {total} SPECIFICATIONS
          </Badge>
        </Group>

        <Group gap="xs">
          <Button
            size="xs"
            color="coral"
            leftSection={<RiPlayCircleLine size={14} />}
            onClick={handleRunAll}
            loading={isRunning}
            disabled={scenarios.length === 0}
          >
            Run All Scenarios
          </Button>
        </Group>
      </Group>

      {/* Progress & Pass Rate Banner */}
      {executedCount > 0 && (
        <Paper p="xs" mb="xs" radius="sm" style={{ background: "var(--paper-3)", border: "1px solid var(--rule)" }}>
          <Group justify="space-between" mb={6}>
            <Group gap="xs">
              <Text size="xs" fw={600} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Test Suite Health:
              </Text>
              <Badge size="xs" color={failedCount === 0 ? "teal" : "red"} variant="filled">
                {passedCount} / {executedCount} PASSED ({passRate}%)
              </Badge>
            </Group>

            <Group gap="xs">
              <RiTimeLine size={12} color="var(--ink-faint)" />
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: 10, color: "var(--ink-faint)" }}>
                Total Duration: {Object.values(results).reduce((a, b) => a + b.durationMs, 0)}ms
              </Text>
            </Group>
          </Group>

          <Progress
            value={passRate}
            color={failedCount === 0 ? "teal" : "red"}
            size="sm"
            radius="xl"
          />
        </Paper>
      )}

      {/* Scenario List */}
      <ScrollArea h={320} offsetScrollbars>
        <Stack gap="xs">
          {scenarios.map((sc) => {
            const res = results[sc.id];
            const hasRun = res !== undefined;

            return (
              <Paper
                key={sc.id}
                p="xs"
                radius="sm"
                style={{
                  background: "var(--paper-2)",
                  border: hasRun
                    ? res.passed
                      ? "1px solid rgba(79, 208, 180, 0.4)"
                      : "1px solid rgba(242, 135, 154, 0.6)"
                    : "1px solid var(--rule)",
                }}
              >
                <Group justify="space-between" mb={4}>
                  <Group gap="xs">
                    {hasRun ? (
                      res.passed ? (
                        <RiCheckFill size={16} color="var(--accent-2)" />
                      ) : (
                        <RiCloseFill size={16} color="var(--accent)" />
                      )
                    ) : (
                      <RiFlaskLine size={16} color="var(--ink-faint)" />
                    )}

                    <Text fw={600} size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink)" }}>
                      {sc.name}
                    </Text>

                    <Badge size="xs" variant="outline" color="gray">
                      {sc.moves.length} moves
                    </Badge>
                  </Group>

                  <Group gap="xs">
                    {hasRun && (
                      <Badge
                        size="xs"
                        variant="light"
                        color={res.passed ? "teal" : "red"}
                        style={{ fontFamily: '"JetBrains Mono", monospace' }}
                      >
                        {res.passed ? `PASSED (${res.durationMs}ms)` : `FAILED (${res.durationMs}ms)`}
                      </Badge>
                    )}

                    <Tooltip label="Run single scenario">
                      <ActionIcon
                        aria-label={"Run scenario " + sc.name}
                        size="xs"
                        variant="subtle"
                        color="coral"
                        onClick={() => handleRunSingle(sc)}
                      >
                        <RiRefreshLine size={12} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                </Group>

                <Text size="xs" style={{ color: "var(--ink-soft)", fontSize: 11, marginBottom: 6 }}>
                  {sc.description}
                </Text>

                {/* Error diagnostics if scenario failed */}
                {hasRun && !res.passed && (
                  <Paper p={6} mt={4} radius="xs" style={{ background: "rgba(242, 135, 154, 0.1)", border: "1px solid rgba(242, 135, 154, 0.3)" }}>
                    <Text size="xs" fw={600} style={{ color: "var(--accent)", fontFamily: '"JetBrains Mono", monospace', fontSize: 11 }}>
                      Assertion Violations:
                    </Text>
                    {res.errors.map((err, errIdx) => (
                      <Text key={errIdx} size="xs" style={{ color: "var(--ink-soft)", fontFamily: '"JetBrains Mono", monospace', fontSize: 10 }}>
                        • {err}
                      </Text>
                    ))}
                  </Paper>
                )}
              </Paper>
            );
          })}
        </Stack>
      </ScrollArea>
    </Card>
  );
};

export default ScenarioTestStudio;
