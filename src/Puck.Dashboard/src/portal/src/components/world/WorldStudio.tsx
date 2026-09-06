import React, { useState, useMemo, useEffect, useRef } from "react";
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
  Alert,
} from "@mantine/core";
import {
  RiRefreshLine,
  RiGamepadLine,
  RiCodeSSlashLine,
  RiUser3Line,
  RiArrowLeftLine,
  RiArrowRightLine,
  RiTrophyLine,
  RiGitMergeLine,
  RiHistoryLine,
  RiFlaskLine,
  RiCpuLine,
  RiCompass3Fill,
  RiTerminalBoxLine,
  RiAddLine,
  RiSparklingLine,
} from "@remixicon/react";

import UniversalTopologyView from "./UniversalTopologyView";
import ReactiveRuleGraph from "./ReactiveRuleGraph";
import StateMatrixView, { StateRowDefinition } from "./StateMatrixView";
import WorldWorkbench from "./WorldWorkbench";
import ExecutionTraceLog from "./ExecutionTraceLog";
import ScenarioTestStudio from "./ScenarioTestStudio";
import MonteCarloStudio from "./MonteCarloStudio";
import LatticeRayStudio from "./LatticeRayStudio";
import PuckReplConsole from "./PuckReplConsole";
import RuleAuthoringModal from "./RuleAuthoringModal";

import {
  TopologyDefinition,
  WorldRule,
  boardShift,
  computeBoardMask,
  getTopologyCoordinates,
} from "../../engine/evaluator";
import { executeSimulationTick, StateDelta } from "../../engine/tickRunner";
import { ReplayTape, TickSnapshot } from "../../engine/replayTape";
import { AVAILABLE_PRESETS, TIC_TAC_TOE_WORLD } from "../../catalog/worldCatalog";

export const WorldStudio: React.FC = () => {
  // World Preset Selection
  const [selectedPresetId, setSelectedPresetId] = useState<string>("tictactoe");
  const [worldJsonText, setWorldJsonText] = useState<string>(
    JSON.stringify(TIC_TAC_TOE_WORLD, null, 2)
  );

  // Parse world data
  const parsedWorld = useMemo(() => {
    try {
      return JSON.parse(worldJsonText);
    } catch {
      return TIC_TAC_TOE_WORLD;
    }
  }, [worldJsonText]);

  const topologies: TopologyDefinition[] = parsedWorld.state?.lattices ?? [];
  const stateDefinitions: StateRowDefinition[] = parsedWorld.state?.world ?? [];
  const rules: WorldRule[] = parsedWorld.rules ?? [];

  const topologyMap = useMemo(() => {
    const map: Record<string, TopologyDefinition> = {};
    topologies.forEach((t) => {
      map[t.name] = t;
    });
    return map;
  }, [topologies]);

  const [selectedTopologyName, setSelectedTopologyName] = useState<string>(
    topologies[0]?.name ?? "tttCube"
  );
  const activeTopology =
    topologies.find((t) => t.name === selectedTopologyName) ?? topologies[0];

  // Replay tape for time-travel
  const replayTapeRef = useRef<ReplayTape | null>(null);
  const [currentSnapshot, setCurrentSnapshot] = useState<TickSnapshot | null>(null);
  const [snapshotsList, setSnapshotsList] = useState<TickSnapshot[]>([]);
  const [hoveredMask, setHoveredMask] = useState<bigint | null>(null);

  // Probed vector beam from LatticeRayStudio
  const [probedRayBeam, setProbedRayBeam] = useState<{ name: string; cells: number[] } | null>(null);

  // Visual Rule Composer Modal
  const [ruleModalOpen, setRuleModalOpen] = useState(false);

  // Hovered cell for speculative ghost move projection
  const [hoveredCell, setHoveredCell] = useState<number | null>(null);

  // Initialize replay tape whenever a new world definition is loaded
  useEffect(() => {
    const initialScalars: Record<string, any> = {};
    const initialCells: Record<string, Record<number, number>> = {};

    stateDefinitions.forEach((s) => {
      if (s.domain) {
        initialCells[s.name] = {};
        if ((s as any).cells && Array.isArray((s as any).cells)) {
          (s as any).cells.forEach((c: any) => {
            initialCells[s.name][Number(c.key)] = Number(c.value);
          });
        }
      } else {
        initialScalars[s.name] = s.value ?? 0;
      }
    });

    const tape = new ReplayTape(initialScalars, initialCells);
    replayTapeRef.current = tape;
    setCurrentSnapshot(tape.getCurrentSnapshot());
    setSnapshotsList(tape.getAllSnapshots());
  }, [worldJsonText]);

  const liveState = currentSnapshot?.state ?? {};
  const liveCells = currentSnapshot?.boardCells?.["tttBoard"] ?? currentSnapshot?.boardCells?.[stateDefinitions[0]?.name ?? ""] ?? {};
  const tickCount = currentSnapshot?.tickNumber ?? 0;
  const activePlayer = liveState["tttActive"] ?? liveState["hexTurn"] ?? liveState["chessTurn"] ?? 1;
  const winner = liveState["tttWinner"] ?? liveState["hexWinner"] ?? 0;
  const lastDeltas: StateDelta[] = currentSnapshot?.trace?.allDeltas ?? [];

  // Speculative Ghost Move Simulation on Hover
  const speculativeResult = useMemo(() => {
    if (hoveredCell === null || winner !== 0 || !currentSnapshot) return null;
    const isOccupied = liveCells[hoveredCell] && liveCells[hoveredCell] !== 0;
    if (isOccupied) {
      return { illegal: true, message: `Cell #${hoveredCell} is already occupied` };
    }

    const moveRequestSeq = Number(liveState["tttMoveRequest"] ?? 0) + 1;
    const sim = executeSimulationTick(
      tickCount + 1,
      `Speculative Move at #${hoveredCell}`,
      {
        tttMoveCell: hoveredCell,
        tttMoveRequest: moveRequestSeq,
        hexMoveCell: hoveredCell,
        hexMoveRequest: moveRequestSeq,
      },
      currentSnapshot.state,
      currentSnapshot.boardCells,
      rules,
      topologyMap
    );

    const nextWinner = sim.nextState["tttWinner"] ?? sim.nextState["hexWinner"] ?? 0;
    const firedRule = sim.trace.ruleEvents.find((r) => r.fired);
    const nextPlayer = sim.nextState["tttActive"] ?? sim.nextState["hexTurn"] ?? 1;

    return {
      illegal: false,
      cell: hoveredCell,
      nextWinner,
      firedRuleName: firedRule?.ruleName ?? "ttt-place-mark",
      nextPlayer,
      isWinningMove: nextWinner > 0,
    };
  }, [hoveredCell, winner, currentSnapshot, liveCells, liveState, rules, topologyMap, tickCount]);

  // Calculate winning ray cells for 3D beam illumination
  const activeWinningRay = useMemo(() => {
    if (!winner || winner === 3 || !activeTopology) return null;

    const boardCells = liveCells;
    const playerVal = winner; // 1 for X, 2 for O
    const mask = computeBoardMask(boardCells, playerVal);
    const coords = getTopologyCoordinates(activeTopology);

    // Test each direction to find which one caused the 4-in-a-row
    for (const dir of activeTopology.directions ?? []) {
      const s1 = boardShift(mask, activeTopology, dir.name);
      const s2 = boardShift(s1 & mask, activeTopology, dir.name);
      const s3 = boardShift(s2 & mask, activeTopology, dir.name);
      const winBits = s3 & mask;

      if (winBits !== 0n) {
        // Find 4 collinear cells along this ray
        const winningIndices: number[] = [];
        for (let i = 0; i < coords.length; i++) {
          if ((winBits & (1n << BigInt(i))) !== 0n) {
            // Found target endpoint, backtrack 3 steps
            winningIndices.push(i);
            const oppositeName = dir.name.endsWith("Back")
              ? dir.name.replace("Back", "")
              : `${dir.name}Back`;
            let curr = i;
            for (let step = 0; step < 3; step++) {
              const singleMask = 1n << BigInt(curr);
              const back = boardShift(singleMask, activeTopology, oppositeName);
              for (let j = 0; j < coords.length; j++) {
                if ((back & (1n << BigInt(j))) !== 0n) {
                  winningIndices.push(j);
                  curr = j;
                  break;
                }
              }
            }
            break;
          }
        }
        if (winningIndices.length > 0) {
          return { name: dir.name, cells: winningIndices };
        }
      }
    }
    return null;
  }, [winner, liveCells, activeTopology]);

  // Combined ray passed to 3D view (winning ray has priority, otherwise probed ray)
  const displayedRayBeam = activeWinningRay ?? probedRayBeam;

  // Handle Preset World Switch
  const handlePresetSelect = (presetId: string | null) => {
    if (!presetId) return;
    setSelectedPresetId(presetId);
    setProbedRayBeam(null);
    const item = AVAILABLE_PRESETS.find((p) => p.id === presetId);
    if (item) {
      setWorldJsonText(JSON.stringify(item.world, null, 2));
    }
  };

  // Dispatch an action and run full Puck simulation tick
  const dispatchAction = (intentDescription: string, mutations: Record<string, any>) => {
    if (!replayTapeRef.current || !currentSnapshot) return;

    const result = executeSimulationTick(
      tickCount + 1,
      intentDescription,
      mutations,
      currentSnapshot.state,
      currentSnapshot.boardCells,
      rules,
      topologyMap
    );

    replayTapeRef.current.recordStep(
      tickCount + 1,
      result.nextState,
      result.nextBoardCells,
      result.trace
    );

    setCurrentSnapshot(replayTapeRef.current.getCurrentSnapshot());
    setSnapshotsList(replayTapeRef.current.getAllSnapshots());
  };

  // Clicking a cell on the topology
  const handleSelectCell = (cellIdx: number) => {
    if (winner !== 0) return; // Game already finished

    const moveRequestSeq = Number(liveState["tttMoveRequest"] ?? 0) + 1;
    dispatchAction(`Place mark at Cell #${cellIdx}`, {
      tttMoveCell: cellIdx,
      tttMoveRequest: moveRequestSeq,
      hexMoveCell: cellIdx,
      hexMoveRequest: moveRequestSeq,
    });
  };

  // Time-travel functions
  const handleUndo = () => {
    if (!replayTapeRef.current) return;
    const snap = replayTapeRef.current.undo();
    if (snap) setCurrentSnapshot(snap);
  };

  const handleRedo = () => {
    if (!replayTapeRef.current) return;
    const snap = replayTapeRef.current.redo();
    if (snap) setCurrentSnapshot(snap);
  };

  const handleJumpToTick = (idx: number) => {
    if (!replayTapeRef.current) return;
    const snap = replayTapeRef.current.jumpTo(idx);
    if (snap) setCurrentSnapshot(snap);
  };

  const handleResetWorld = () => {
    const initialScalars: Record<string, any> = {};
    const initialCells: Record<string, Record<number, number>> = {};

    stateDefinitions.forEach((s) => {
      if (s.domain) {
        initialCells[s.name] = {};
      } else {
        initialScalars[s.name] = s.value ?? 0;
      }
    });

    replayTapeRef.current?.reset(initialScalars, initialCells);
    if (replayTapeRef.current) {
      setCurrentSnapshot(replayTapeRef.current.getCurrentSnapshot());
      setSnapshotsList(replayTapeRef.current.getAllSnapshots());
    }
  };

  const handleStateChange = (stateName: string, newValue: any) => {
    dispatchAction(`Manual tweak: ${stateName} = ${newValue}`, {
      [stateName]: newValue,
    });
  };

  const handleSaveCustomRule = (newRule: WorldRule) => {
    try {
      const currentWorld = JSON.parse(worldJsonText);
      const updatedRules = [...(currentWorld.rules ?? []), newRule];
      const updatedWorld = { ...currentWorld, rules: updatedRules };
      setWorldJsonText(JSON.stringify(updatedWorld, null, 2));
    } catch {
      // ignore
    }
  };

  return (
    <Box p="md">
      {/* Studio Header Toolbar */}
      <Paper p="sm" radius="md" mb="md" style={{ background: "var(--paper-2)", border: "1px solid var(--rule)" }}>
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Group gap="sm">
            <RiGamepadLine size={26} color="var(--accent)" />
            <div>
              <div className="kicker">Puck World Simulation IDE</div>
              <Group gap={8} align="baseline">
                <Text fw={600} size="md" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
                  Formal State Simulation, CAD & Rule Studio
                </Text>
              </Group>
            </div>
          </Group>

          {/* Preset Selector */}
          <Group gap="xs">
            <Select
              size="xs"
              label="World Preset"
              value={selectedPresetId}
              onChange={handlePresetSelect}
              data={AVAILABLE_PRESETS.map((p) => ({ label: `${p.name} (${p.category})`, value: p.id }))}
              style={{ width: 250 }}
            />

            {topologies.length > 1 && (
              <Select
                size="xs"
                label="Active Topology"
                value={selectedTopologyName}
                onChange={(val) => val && setSelectedTopologyName(val)}
                data={topologies.map((t) => ({ label: `${t.name} (${t.$type})`, value: t.name }))}
                style={{ width: 170 }}
              />
            )}

            <Button
              size="xs"
              variant="outline"
              color="jade"
              leftSection={<RiAddLine size={14} />}
              onClick={() => setRuleModalOpen(true)}
              style={{ alignSelf: "flex-end" }}
            >
              New Rule
            </Button>
          </Group>

          {/* Simulation & Time-Travel Controls */}
          <Group gap="xs" align="flex-end">
            <Badge
              variant="light"
              color={activePlayer === 1 ? "coral" : "jade"}
              size="md"
              leftSection={<RiUser3Line size={13} />}
            >
              Turn: Player {activePlayer} ({activePlayer === 1 ? "Rose-Coral 'X'" : "Jade 'O'"})
            </Badge>

            <Button
              size="xs"
              variant="default"
              leftSection={<RiArrowLeftLine size={14} />}
              onClick={handleUndo}
              disabled={!replayTapeRef.current?.canUndo()}
            >
              Undo
            </Button>

            <Badge variant="filled" color="coral" size="md">
              Tick #{tickCount}
            </Badge>

            <Button
              size="xs"
              variant="default"
              rightSection={<RiArrowRightLine size={14} />}
              onClick={handleRedo}
              disabled={!replayTapeRef.current?.canRedo()}
            >
              Redo
            </Button>

            <Button
              size="xs"
              variant="light"
              color="coral"
              leftSection={<RiRefreshLine size={14} />}
              onClick={handleResetWorld}
            >
              Reset
            </Button>
          </Group>
        </Group>
      </Paper>

      {/* Game Over / Winner Banner */}
      {winner !== 0 && (
        <Alert
          icon={<RiTrophyLine size={20} />}
          title={winner === 3 ? "Game Concluded: Draw" : `Victory: Player ${winner} (${winner === 1 ? "Rose-Coral 'X'" : "Jade 'O'"}) Wins!`}
          color={winner === 1 ? "coral" : winner === 2 ? "jade" : "gray"}
          mb="md"
          style={{
            background: "var(--quote-bg)",
            border: "1px solid var(--rule)",
            borderLeft: `4px solid ${winner === 1 ? "var(--accent)" : winner === 2 ? "var(--accent-2)" : "var(--rule)"}`,
          }}
        >
          <Text size="sm" style={{ fontFamily: '"Lora", Georgia, serif' }}>
            {activeWinningRay
              ? `Four collinear marks verified along lattice ray ${activeWinningRay.name}! Glowing vector is illuminated on the 3D topology.`
              : "World reached terminal win state according to Level reactive rules."}
          </Text>
        </Alert>
      )}

      {/* Speculative Ghost Hover HUD Alert */}
      {speculativeResult && winner === 0 && (
        <Alert
          icon={<RiSparklingLine size={18} color={speculativeResult.isWinningMove ? "var(--accent)" : "var(--accent-2)"} />}
          color={speculativeResult.isWinningMove ? "yellow" : speculativeResult.illegal ? "red" : "teal"}
          variant="light"
          mb="md"
          title={`Speculative Move Preview [Cell #${hoveredCell}]`}
        >
          {speculativeResult.illegal ? (
            <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
              Illegal Action: {speculativeResult.message}
            </Text>
          ) : (
            <Group gap="md">
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Rule: <strong>{speculativeResult.firedRuleName}</strong> will fire
              </Text>
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
                Next Turn: Player {speculativeResult.nextPlayer}
              </Text>
              {speculativeResult.isWinningMove && (
                <Badge size="xs" color="yellow" variant="filled">
                  CRITICAL WINNING MOVE!
                </Badge>
              )}
            </Group>
          )}
        </Alert>
      )}

      {/* Main Studio Two-Column Grid */}
      <Grid gap="md">
        {/* Left Column: Spatial Topology CAD & State Matrix (7 Cols) */}
        <Grid.Col span={{ base: 12, md: 7 }}>
          <Stack gap="md">
            {activeTopology ? (
              <UniversalTopologyView
                topology={activeTopology}
                cellValues={liveCells}
                selectedCell={liveState["tttMoveCell"]}
                onSelectCell={handleSelectCell}
                hoveredMask={hoveredMask}
                activeWinningRay={displayedRayBeam}
                ghostCell={hoveredCell}
                ghostPlayer={activePlayer}
                onHoverCell={setHoveredCell}
              />
            ) : (
              <Text c="var(--ink-faint)" size="sm">
                No topology declared in this world.
              </Text>
            )}

            <StateMatrixView
              stateDefinitions={stateDefinitions}
              currentState={liveState}
              lastDeltas={lastDeltas}
              onStateChange={handleStateChange}
              onResetState={handleResetWorld}
              onHoverMask={setHoveredMask}
            />
          </Stack>
        </Grid.Col>

        {/* Right Column: Advanced Simulation Subsystem Tabs (5 Cols) */}
        <Grid.Col span={{ base: 12, md: 5 }}>
          <Tabs defaultValue="trace" variant="outline">
            <Tabs.List mb="xs" style={{ flexWrap: "wrap" }}>
              <Tabs.Tab value="trace" leftSection={<RiHistoryLine size={15} />}>
                Trace Log
              </Tabs.Tab>
              <Tabs.Tab value="scenarios" leftSection={<RiFlaskLine size={15} />}>
                Test Suite
              </Tabs.Tab>
              <Tabs.Tab value="montecarlo" leftSection={<RiCpuLine size={15} />}>
                Monte Carlo
              </Tabs.Tab>
              <Tabs.Tab value="rays" leftSection={<RiCompass3Fill size={15} />}>
                Ray Prober
              </Tabs.Tab>
              <Tabs.Tab value="repl" leftSection={<RiTerminalBoxLine size={15} />}>
                REPL
              </Tabs.Tab>
              <Tabs.Tab value="rules" leftSection={<RiGitMergeLine size={15} />}>
                Rule DAG
              </Tabs.Tab>
              <Tabs.Tab value="workbench" leftSection={<RiCodeSSlashLine size={15} />}>
                JSON
              </Tabs.Tab>
            </Tabs.List>

            {/* Tab 1: Causal Execution Trace with Predicate Truth Trees */}
            <Tabs.Panel value="trace">
              <ExecutionTraceLog
                snapshots={snapshotsList}
                currentTickIndex={replayTapeRef.current?.getCurrentIndex() ?? 0}
                onJumpToTick={handleJumpToTick}
              />
            </Tabs.Panel>

            {/* Tab 2: Scenario Test Suite */}
            <Tabs.Panel value="scenarios">
              <ScenarioTestStudio
                worldId={selectedPresetId}
                rules={rules}
                topologies={topologyMap}
              />
            </Tabs.Panel>

            {/* Tab 3: Monte Carlo Batch Rollout & Rule Health */}
            <Tabs.Panel value="montecarlo">
              <MonteCarloStudio
                rules={rules}
                topologies={topologyMap}
                initialState={currentSnapshot?.state ?? {}}
                initialBoardCells={currentSnapshot?.boardCells ?? {}}
              />
            </Tabs.Panel>

            {/* Tab 4: 3D Lattice Ray Prober */}
            <Tabs.Panel value="rays">
              {activeTopology ? (
                <LatticeRayStudio
                  topology={activeTopology}
                  onSelectRayBeam={(rayName, cells) => setProbedRayBeam({ name: rayName, cells })}
                  activeRayName={displayedRayBeam?.name}
                />
              ) : (
                <Text size="xs" c="dimmed">No active topology to probe.</Text>
              )}
            </Tabs.Panel>

            {/* Tab 5: Interactive Expression REPL */}
            <Tabs.Panel value="repl">
              <PuckReplConsole
                state={liveState}
                boardCells={currentSnapshot?.boardCells ?? {}}
                topologies={topologyMap}
              />
            </Tabs.Panel>

            {/* Tab 6: Reactive Rule Dependency DAG */}
            <Tabs.Panel value="rules">
              <ReactiveRuleGraph rules={rules} currentState={liveState} />
            </Tabs.Panel>

            {/* Tab 7: Raw World JSON Workbench */}
            <Tabs.Panel value="workbench">
              <WorldWorkbench
                worldJson={worldJsonText}
                onWorldJsonChange={(newJson) => setWorldJsonText(newJson)}
              />
            </Tabs.Panel>
          </Tabs>
        </Grid.Col>
      </Grid>

      {/* Visual Rule Authoring Modal Dialog */}
      <RuleAuthoringModal
        opened={ruleModalOpen}
        onClose={() => setRuleModalOpen(false)}
        onSaveRule={handleSaveCustomRule}
        existingStateVars={stateDefinitions.map((s) => s.name)}
        topologyNames={topologies.map((t) => t.name)}
      />
    </Box>
  );
};

export default WorldStudio;
