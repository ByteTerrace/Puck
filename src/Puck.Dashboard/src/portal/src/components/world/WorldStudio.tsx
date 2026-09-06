import React, { useState, useMemo } from "react";
import {
  Box,
  Grid,
  Group,
  Stack,
  Text,
  Badge,
  Select,
  Button,
  Tabs,
  Paper,
} from "@mantine/core";
import {
  RiPlayFill,
  RiRefreshLine,
  RiGamepadLine,
  RiCodeSSlashLine,
} from "@remixicon/react";

import UniversalTopologyView, { TopologyDefinition } from "./UniversalTopologyView";
import ReactiveRuleGraph, { WorldRule } from "./ReactiveRuleGraph";
import StateMatrixView, { StateRowDefinition } from "./StateMatrixView";
import WorldWorkbench from "./WorldWorkbench";

// Sample pre-loaded world: Qubic 4x4x4 Tic-Tac-Toe
const sampleTicTacToeWorld = {
  rules: [
    {
      name: "ttt-place-mark",
      mode: "Edge",
      gate: {
        $type: "all",
        predicates: [
          { $type: "compareState", state: "tttMoveRequest", comparandState: "tttMoveApplied", comparison: "NotEqual" },
          { $type: "compareState", state: "tttWinner", value: 0, comparison: "Equal" },
          { $type: "compareState", state: "tttMoveCell", value: 0, comparison: "GreaterOrEqual" },
          { $type: "compareState", state: "tttMoveCell", value: 63, comparison: "LessOrEqual" },
        ],
      },
      effects: [
        { $type: "setState", state: "tttBoard", fromState: "tttActive", key: "$cell:tttMoveCell:$value" },
        { $type: "addState", state: "tttMoveCount", value: 1 },
        { $type: "addState", state: "tttBoardVersion", value: 1 },
        { $type: "setState", state: "tttMoveApplied", fromState: "tttMoveRequest" },
        { $type: "setState", state: "tttActive", expression: "3 - tttActive" },
      ],
    },
    {
      name: "ttt-check-win",
      mode: "Level",
      gate: {
        $type: "compareState",
        state: "tttWinner",
        value: 0,
        comparison: "Equal",
      },
      effects: [
        { $type: "setState", state: "tttMaskX", expression: "$board:mask:tttBoard:1:1" },
        { $type: "setState", state: "tttMaskO", expression: "$board:mask:tttBoard:2:2" },
      ],
    },
  ],
  state: {
    lattices: [
      {
        $type: "lattice",
        name: "tttCube",
        dimensions: { x: 4, y: 4, z: 4 },
        coordinates: Array.from({ length: 64 }, (_, i) => ({
          x: i % 4,
          y: Math.floor((i % 16) / 4),
          z: Math.floor(i / 16),
        })),
        directions: [
          { name: "L0", x: 0, y: 0, z: 1 },
          { name: "L0Back", x: 0, y: 0, z: -1 },
          { name: "L1", x: 0, y: 1, z: 0 },
          { name: "L1Back", x: 0, y: 0, z: -1 },
        ],
      },
    ],
    world: [
      { name: "tttBoard", kind: "int", domain: { $type: "cellsOf", topology: "tttCube" } },
      { name: "tttActive", kind: "int", min: 1, max: 2, value: 1 },
      { name: "tttMoveCell", kind: "int", min: -1, max: 63, value: -1 },
      { name: "tttMoveRequest", kind: "int", nonNegative: true, value: 0 },
      { name: "tttMoveApplied", kind: "int", nonNegative: true, value: 0 },
      { name: "tttBoardVersion", kind: "int", nonNegative: true, value: 0 },
      { name: "tttWinner", kind: "int", min: 0, max: 3, value: 0 },
      { name: "tttMoveCount", kind: "int", min: 0, max: 64, value: 0 },
      { name: "tttMaskX", kind: "int", value: 0 },
      { name: "tttMaskO", kind: "int", value: 0 },
    ],
  },
};

export const WorldStudio: React.FC = () => {
  const [worldJsonText, setWorldJsonText] = useState<string>(
    JSON.stringify(sampleTicTacToeWorld, null, 2)
  );

  // Parse world data
  const parsedWorld = useMemo(() => {
    try {
      return JSON.parse(worldJsonText);
    } catch {
      return sampleTicTacToeWorld;
    }
  }, [worldJsonText]);

  const topologies: TopologyDefinition[] = parsedWorld.state?.lattices ?? [];
  const stateDefinitions: StateRowDefinition[] = parsedWorld.state?.world ?? [];
  const rules: WorldRule[] = parsedWorld.rules ?? [];

  // Selected topology
  const [selectedTopologyName, setSelectedTopologyName] = useState<string>(
    topologies[0]?.name ?? "tttCube"
  );
  const activeTopology =
    topologies.find((t) => t.name === selectedTopologyName) ?? topologies[0];

  // Simulation State Sandbox
  const [liveState, setLiveState] = useState<Record<string, any>>(() => {
    const initial: Record<string, any> = {};
    stateDefinitions.forEach((s) => {
      initial[s.name] = s.value ?? 0;
    });
    return initial;
  });

  const [boardCells, setBoardCells] = useState<Record<number, number>>({});
  const [selectedCell, setSelectedCell] = useState<number | null>(null);
  const [tickCount, setTickCount] = useState<number>(0);

  const handleStateChange = (stateName: string, newValue: any) => {
    setLiveState((prev) => ({ ...prev, [stateName]: newValue }));
  };

  const handleResetState = () => {
    const initial: Record<string, any> = {};
    stateDefinitions.forEach((s) => {
      initial[s.name] = s.value ?? 0;
    });
    setLiveState(initial);
    setBoardCells({});
    setSelectedCell(null);
    setTickCount(0);
  };

  // Clicking a cell on the topology injects a move
  const handleSelectCell = (cellIdx: number) => {
    setSelectedCell(cellIdx);
    const activePlayer = liveState["tttActive"] ?? 1;

    // Place mark
    setBoardCells((prev) => ({
      ...prev,
      [cellIdx]: prev[cellIdx] ? prev[cellIdx] : activePlayer,
    }));

    // Update state registers
    setLiveState((prev) => {
      const nextActive = activePlayer === 1 ? 2 : 1;
      const moveCount = (prev["tttMoveCount"] ?? 0) + 1;
      const req = (prev["tttMoveRequest"] ?? 0) + 1;

      return {
        ...prev,
        tttMoveCell: cellIdx,
        tttMoveRequest: req,
        tttMoveApplied: req,
        tttActive: nextActive,
        tttMoveCount: moveCount,
        tttBoardVersion: (prev["tttBoardVersion"] ?? 0) + 1,
      };
    });

    setTickCount((t) => t + 1);
  };

  return (
    <Box p="md">
      {/* Studio Header */}
      <Paper p="sm" radius="md" mb="md" style={{ background: "var(--mantine-color-dark-8)" }}>
        <Group justify="space-between">
          <Group gap="sm">
            <RiGamepadLine size={24} color="#38bdf8" />
            <div>
              <Text fw={700} size="md">
                Puck World Studio
              </Text>
              <Text size="xs" c="dimmed">
                Interactive zero-3D state inspection, reactive rule dataflow & topology CAD
              </Text>
            </div>
          </Group>

          <Group gap="sm">
            <Badge variant="filled" color="indigo" size="md">
              Tick #{tickCount}
            </Badge>
            <Button
              size="xs"
              variant="light"
              color="teal"
              leftSection={<RiPlayFill size={14} />}
              onClick={() => setTickCount((t) => t + 1)}
            >
              Step Tick
            </Button>
            <Button
              size="xs"
              variant="default"
              leftSection={<RiRefreshLine size={14} />}
              onClick={handleResetState}
            >
              Reset World
            </Button>
          </Group>
        </Group>
      </Paper>

      {/* Main Workspace Tabs */}
      <Tabs defaultValue="studio" variant="outline">
        <Tabs.List mb="md">
          <Tabs.Tab value="studio" leftSection={<RiGamepadLine size={16} />}>
            Interactive World Studio
          </Tabs.Tab>
          <Tabs.Tab value="workbench" leftSection={<RiCodeSSlashLine size={16} />}>
            Definition Workbench & Linter
          </Tabs.Tab>
        </Tabs.List>

        {/* Tab 1: Studio */}
        <Tabs.Panel value="studio">
          <Grid gap="md">
            {/* Left Column: Topology Projector */}
            <Grid.Col span={{ base: 12, md: 7 }}>
              <Stack gap="md">
                {topologies.length > 1 && (
                  <Select
                    size="xs"
                    label="Active Topology"
                    value={selectedTopologyName}
                    onChange={(val) => val && setSelectedTopologyName(val)}
                    data={topologies.map((t) => ({ label: `${t.name} (${t.$type})`, value: t.name }))}
                  />
                )}

                {activeTopology ? (
                  <UniversalTopologyView
                    topology={activeTopology}
                    cellValues={boardCells}
                    selectedCell={selectedCell}
                    onSelectCell={handleSelectCell}
                  />
                ) : (
                  <Text c="dimmed" size="sm">
                    No topology declared in this world.
                  </Text>
                )}

                <StateMatrixView
                  stateDefinitions={stateDefinitions}
                  currentState={liveState}
                  onStateChange={handleStateChange}
                  onResetState={handleResetState}
                />
              </Stack>
            </Grid.Col>

            {/* Right Column: Rule DAG & Gate Telemetry */}
            <Grid.Col span={{ base: 12, md: 5 }}>
              <ReactiveRuleGraph rules={rules} currentState={liveState} />
            </Grid.Col>
          </Grid>
        </Tabs.Panel>

        {/* Tab 2: Workbench */}
        <Tabs.Panel value="workbench">
          <WorldWorkbench
            worldJson={worldJsonText}
            onWorldJsonChange={(newJson) => setWorldJsonText(newJson)}
          />
        </Tabs.Panel>
      </Tabs>
    </Box>
  );
};

export default WorldStudio;
