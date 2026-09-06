import React, { useState } from "react";
import {
  Card,
  Group,
  Stack,
  Text,
  Badge,
  Button,
  Paper,
  Slider,
  SegmentedControl,
  Table,
  Progress,
  Alert,
  SimpleGrid,
  Box,
} from "@mantine/core";
import {
  RiCpuLine,
  RiPlayCircleLine,
  RiBarChartGroupedLine,
  RiAlertLine,
  RiTimeLine,
} from "@remixicon/react";
import { WorldRule, TopologyDefinition } from "../../engine/evaluator";
import {
  runMonteCarloRollout,
  MonteCarloSummary,
} from "../../engine/scenarioRunner";

export interface MonteCarloStudioProps {
  rules: WorldRule[];
  topologies: Record<string, TopologyDefinition>;
  initialState: Record<string, any>;
  initialBoardCells: Record<string, Record<number, number>>;
}

export const MonteCarloStudio: React.FC<MonteCarloStudioProps> = ({
  rules,
  topologies,
  initialState,
  initialBoardCells,
}) => {
  const [gameCount, setGameCount] = useState<number>(50);
  const [policy, setPolicy] = useState<"random" | "greedy">("random");
  const [isRunning, setIsRunning] = useState<boolean>(false);
  const [summary, setSummary] = useState<MonteCarloSummary | null>(null);

  const handleRunRollout = () => {
    setIsRunning(true);
    setTimeout(() => {
      const res = runMonteCarloRollout(
        gameCount,
        rules,
        topologies,
        initialState,
        initialBoardCells,
        { policy }
      );
      setSummary(res);
      setIsRunning(false);
    }, 40);
  };

  const p1Pct = summary && summary.simulationsCount > 0 ? Math.round((summary.player1Wins / summary.simulationsCount) * 100) : 0;
  const p2Pct = summary && summary.simulationsCount > 0 ? Math.round((summary.player2Wins / summary.simulationsCount) * 100) : 0;
  const drawPct = summary && summary.simulationsCount > 0 ? Math.round((summary.draws / summary.simulationsCount) * 100) : 0;

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      {/* Header */}
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiCpuLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Monte Carlo Rollout & Rule Health Profiler
          </Text>
          <Badge variant="light" color="jade" size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
            BATCH SIMULATOR
          </Badge>
        </Group>

        <Button
          size="xs"
          color="coral"
          leftSection={<RiPlayCircleLine size={14} />}
          onClick={handleRunRollout}
          loading={isRunning}
        >
          Simulate {gameCount} Matches
        </Button>
      </Group>

      {/* Control Configuration Bar */}
      <Paper p="xs" mb="xs" radius="sm" style={{ background: "var(--paper-3)", border: "1px solid var(--rule)" }}>
        <Group justify="space-between" align="center">
          <Group gap="md" style={{ flex: 1 }}>
            <Box style={{ width: 220 }}>
              <Text size="xs" fw={600} mb={4} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Batch Size: {gameCount} Games
              </Text>
              <Slider
                size="xs"
                color="coral"
                value={gameCount}
                onChange={setGameCount}
                min={10}
                max={200}
                step={10}
                marks={[
                  { value: 10, label: "10" },
                  { value: 50, label: "50" },
                  { value: 100, label: "100" },
                  { value: 200, label: "200" },
                ]}
              />
            </Box>

            <Box>
              <Text size="xs" fw={600} mb={4} style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Agent Policy:
              </Text>
              <SegmentedControl
                size="xs"
                value={policy}
                onChange={(val: any) => setPolicy(val)}
                data={[
                  { label: "Uniform Random", value: "random" },
                  { label: "Greedy Threat-Winner", value: "greedy" },
                ]}
              />
            </Box>
          </Group>

          {summary && (
            <Group gap="xs">
              <RiTimeLine size={14} color="var(--ink-faint)" />
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-soft)" }}>
                Completed in {summary.durationMs}ms ({summary.averageTicks} avg ticks/game)
              </Text>
            </Group>
          )}
        </Group>
      </Paper>

      {/* Rollout Analytics Results */}
      {summary && (
        <Stack gap="xs">
          {/* Deadlock Warning Alert if any */}
          {summary.deadlocksDetected > 0 && (
            <Alert
              icon={<RiAlertLine size={16} />}
              title="Livelock / Deadlock Detected!"
              color="red"
              variant="light"
            >
              {summary.deadlocksDetected} of {summary.simulationsCount} games reached max tick limits without terminating or updating game state. Check for missing termination rules or circular guards.
            </Alert>
          )}

          {/* Outcome Distribution Cards */}
          <SimpleGrid cols={{ base: 1, sm: 4 }} spacing="xs">
            <Paper p="xs" radius="sm" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Text size="xs" c="var(--ink-faint)" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                PLAYER 1 (ROSE-CORAL)
              </Text>
              <Text size="lg" fw={700} style={{ color: "var(--accent)", fontFamily: '"JetBrains Mono", monospace' }}>
                {summary.player1Wins} ({p1Pct}%)
              </Text>
              <Progress value={p1Pct} color="coral" size="xs" mt={4} />
            </Paper>

            <Paper p="xs" radius="sm" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Text size="xs" c="var(--ink-faint)" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                PLAYER 2 (JADE)
              </Text>
              <Text size="lg" fw={700} style={{ color: "var(--accent-2)", fontFamily: '"JetBrains Mono", monospace' }}>
                {summary.player2Wins} ({p2Pct}%)
              </Text>
              <Progress value={p2Pct} color="teal" size="xs" mt={4} />
            </Paper>

            <Paper p="xs" radius="sm" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Text size="xs" c="var(--ink-faint)" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                DRAWS / TIES
              </Text>
              <Text size="lg" fw={700} style={{ color: "var(--ink)", fontFamily: '"JetBrains Mono", monospace' }}>
                {summary.draws} ({drawPct}%)
              </Text>
              <Progress value={drawPct} color="gray" size="xs" mt={4} />
            </Paper>

            <Paper p="xs" radius="sm" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Text size="xs" c="var(--ink-faint)" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                AVG TICKS TO END
              </Text>
              <Text size="lg" fw={700} style={{ color: "var(--ink)", fontFamily: '"JetBrains Mono", monospace' }}>
                {summary.averageTicks}
              </Text>
              <Text size="xs" c="dimmed" style={{ fontSize: 10 }}>
                Across {summary.simulationsCount} rollouts
              </Text>
            </Paper>
          </SimpleGrid>

          {/* Rule Health & Coverage Matrix */}
          <Paper p="xs" radius="sm" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
            <Group justify="space-between" mb="xs">
              <Group gap="xs">
                <RiBarChartGroupedLine size={14} color="var(--accent-2)" />
                <Text fw={600} size="xs" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
                  Rule Execution Coverage Matrix
                </Text>
              </Group>
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: 10, color: "var(--ink-faint)" }}>
                Flags dead code and unexercised rules
              </Text>
            </Group>

            <Table striped highlightOnHover withTableBorder={false} style={{ fontSize: 11, fontFamily: '"JetBrains Mono", monospace' }}>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Rule Name</Table.Th>
                  <Table.Th>Mode</Table.Th>
                  <Table.Th>Coverage %</Table.Th>
                  <Table.Th>Total Fires</Table.Th>
                  <Table.Th>Status</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {Object.values(summary.ruleCoverage).map((cov) => {
                  const isDead = cov.coveragePercentage === 0;
                  const isHealthy = cov.coveragePercentage > 50;

                  return (
                    <Table.Tr key={cov.ruleName}>
                      <Table.Td style={{ fontWeight: 600, color: "var(--ink)" }}>
                        {cov.ruleName}
                      </Table.Td>
                      <Table.Td>
                        <Badge size="xs" variant="light" color={cov.mode === "Edge" ? "coral" : "jade"}>
                          {cov.mode}
                        </Badge>
                      </Table.Td>
                      <Table.Td>
                        <Group gap="xs" wrap="nowrap">
                          <Progress
                            value={cov.coveragePercentage}
                            color={isHealthy ? "teal" : isDead ? "red" : "yellow"}
                            size="sm"
                            style={{ width: 80 }}
                          />
                          <Text size="xs">{cov.coveragePercentage}%</Text>
                        </Group>
                      </Table.Td>
                      <Table.Td>{cov.timesFired}</Table.Td>
                      <Table.Td>
                        {isDead ? (
                          <Badge size="xs" color="red" variant="filled">
                            DEAD / UNEXERCISED
                          </Badge>
                        ) : (
                          <Badge size="xs" color="teal" variant="light">
                            ACTIVE ({cov.gamesFiredIn} games)
                          </Badge>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  );
                })}
              </Table.Tbody>
            </Table>
          </Paper>
        </Stack>
      )}
    </Card>
  );
};

export default MonteCarloStudio;
