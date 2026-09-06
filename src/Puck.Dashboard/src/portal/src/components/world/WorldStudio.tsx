import React, { useState } from "react";
import { Box, Grid, Stack, Text, Tabs } from "@mantine/core";
import {
  RiHistoryLine,
  RiFlaskLine,
  RiCpuLine,
  RiCompass3Fill,
  RiTerminalBoxLine,
  RiGitMergeLine,
  RiCodeSSlashLine,
} from "@remixicon/react";

import { SimulationContext } from "../../context/SimulationContext";
import {
  selectActivePlayer,
  selectCurrentTickIndex,
  selectCurrentSnapshot,
  selectSnapshotsList,
  selectLiveState,
  selectLiveCells,
  selectTopologies,
  selectTopologyMap,
  selectActiveTopology,
  selectStateDefinitions,
  selectRules,
  selectWorldJsonText,
  selectHoveredCell,
  selectHoveredMask,
  selectDisplayedRayBeam,
  selectLastDeltas,
} from "../../machines/worldSimulationMachine";

import UniversalTopologyView from "./UniversalTopologyView";
import ReactiveRuleGraph from "./ReactiveRuleGraph";
import StateMatrixView from "./StateMatrixView";
import WorldWorkbench from "./WorldWorkbench";
import ExecutionTraceLog from "./ExecutionTraceLog";
import ScenarioTestStudio from "./ScenarioTestStudio";
import MonteCarloStudio from "./MonteCarloStudio";
import LatticeRayStudio from "./LatticeRayStudio";
import PuckReplConsole from "./PuckReplConsole";
import RuleAuthoringModal from "./RuleAuthoringModal";
import { WorldVaultModal } from "./vault/WorldVaultModal";
import { SaveWorldModal } from "./vault/SaveWorldModal";
import { TopologyDesignerModal } from "./authoring/TopologyDesignerModal";
import { StateSchemaDesignerModal } from "./authoring/StateSchemaDesignerModal";
import { WorldHudDesignerModal, WorldHudConfig } from "./authoring/WorldHudDesignerModal";
import { WorldStudioHeader, StudioModalType } from "./WorldStudioHeader";
import { WorldStudioAlerts } from "./WorldStudioAlerts";
import { defaultWorldStorageClient, WorldMetadata } from "../../clients/worldStorageClient";
import { TopologyDefinition, WorldRule } from "../../engine/evaluator";
import { StateRowDefinition } from "./StateMatrixView";

const WorldStudioInner: React.FC = () => {
  const simActor = SimulationContext.useActorRef();

  // High-performance XState slice selectors
  const activePlayer = SimulationContext.useSelector(selectActivePlayer);
  const currentTickIndex = SimulationContext.useSelector(selectCurrentTickIndex);
  const currentSnapshot = SimulationContext.useSelector(selectCurrentSnapshot);
  const snapshotsList = SimulationContext.useSelector(selectSnapshotsList);
  const liveState = SimulationContext.useSelector(selectLiveState);
  const liveCells = SimulationContext.useSelector(selectLiveCells);
  const topologies = SimulationContext.useSelector(selectTopologies);
  const topologyMap = SimulationContext.useSelector(selectTopologyMap);
  const activeTopology = SimulationContext.useSelector(selectActiveTopology);
  const stateDefinitions = SimulationContext.useSelector(selectStateDefinitions);
  const rules = SimulationContext.useSelector(selectRules);
  const worldJsonText = SimulationContext.useSelector(selectWorldJsonText);
  const hoveredCell = SimulationContext.useSelector(selectHoveredCell);
  const hoveredMask = SimulationContext.useSelector(selectHoveredMask);
  const displayedRayBeam = SimulationContext.useSelector(selectDisplayedRayBeam);
  const lastDeltas = SimulationContext.useSelector(selectLastDeltas);

  // Studio UI Modals (discriminated union prevents conflicting open modals)
  const [activeModal, setActiveModal] = useState<StudioModalType>(null);
  const [selectedPresetId, setSelectedPresetId] = useState<string>("tictactoe");

  // Storage Substrate Metadata
  const [activeWorldId, setActiveWorldId] = useState<string>("tictactoe");
  const [worldMetadata, setWorldMetadata] = useState<Partial<WorldMetadata>>({
    name: "Qubic 3D 4x4x4 Tic-Tac-Toe",
    author: "ByteTerrace",
    description: "Standard 3D Qubic on a 4x4x4 lattice substrate.",
    category: "Strategy",
    visibility: "public",
  });

  const handleSelectPreset = (presetId: string, metadata: Partial<WorldMetadata>) => {
    setSelectedPresetId(presetId);
    setActiveWorldId(presetId);
    setWorldMetadata(metadata);
  };

  const handleSaveWorld = async (id: string, meta: Partial<WorldMetadata>) => {
    const parsed = JSON.parse(worldJsonText);
    await defaultWorldStorageClient.saveWorld(id, parsed, meta);
    setActiveWorldId(id);
    setWorldMetadata((prev) => ({ ...prev, ...meta, name: meta.name || prev.name }));
    simActor.send({ type: "SET_DIRTY", isDirty: false });
  };

  const handleLoadWorld = (worldData: any, meta: any) => {
    const json = JSON.stringify(worldData, null, 2);
    simActor.send({ type: "LOAD_WORLD", worldJsonText: json });
    if (meta?.id) {
      setActiveWorldId(meta.id);
      setSelectedPresetId(meta.id);
    }
    if (meta) setWorldMetadata(meta);
  };

  return (
    <Box p="md">
      {/* Studio Header Toolbar & Controls */}
      <WorldStudioHeader
        selectedPresetId={selectedPresetId}
        onSelectPreset={handleSelectPreset}
        onOpenModal={setActiveModal}
      />

      {/* Dynamic Alerts (Game Over & Speculative Hover Preview) */}
      <WorldStudioAlerts />

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
                onSelectCell={(cellIdx) => simActor.send({ type: "CELL_CLICK", cellIdx })}
                hoveredMask={hoveredMask}
                activeWinningRay={displayedRayBeam}
                ghostCell={hoveredCell}
                ghostPlayer={activePlayer}
                onHoverCell={(cellIdx) => simActor.send({ type: "HOVER_CELL", cellIdx })}
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
              onStateChange={(stateName, newValue) =>
                simActor.send({ type: "STATE_CHANGE", stateName, newValue })
              }
              onResetState={() => simActor.send({ type: "RESET_WORLD" })}
              onHoverMask={(mask) => simActor.send({ type: "HOVER_MASK", mask })}
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
                currentTickIndex={currentTickIndex}
                onJumpToTick={(tickIndex) => simActor.send({ type: "JUMP_TO_TICK", tickIndex })}
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
                  onSelectRayBeam={(rayName, cells) =>
                    simActor.send({ type: "PROBE_RAY", ray: { name: rayName, cells } })
                  }
                  activeRayName={displayedRayBeam?.name}
                />
              ) : (
                <Text size="xs" c="dimmed">
                  No active topology to probe.
                </Text>
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
                onWorldJsonChange={(newJson) => {
                  simActor.send({ type: "LOAD_WORLD", worldJsonText: newJson });
                  simActor.send({ type: "SET_DIRTY", isDirty: true });
                }}
              />
            </Tabs.Panel>
          </Tabs>
        </Grid.Col>
      </Grid>

      {/* Visual Rule Authoring Modal Dialog */}
      <RuleAuthoringModal
        opened={activeModal === "rule"}
        onClose={() => setActiveModal(null)}
        onSaveRule={(newRule: WorldRule) => simActor.send({ type: "ADD_RULE", rule: newRule })}
        existingStateVars={stateDefinitions.map((s) => s.name)}
        topologyNames={topologies.map((t) => t.name)}
      />

      {/* Cloud World Vault & Checkpoints Modal */}
      <WorldVaultModal
        opened={activeModal === "vault"}
        onClose={() => setActiveModal(null)}
        storageClient={defaultWorldStorageClient}
        activeWorldId={activeWorldId}
        onLoadWorld={handleLoadWorld}
        onOpenSaveModal={() => setActiveModal("save")}
      />

      {/* Save World Modal */}
      <SaveWorldModal
        opened={activeModal === "save"}
        onClose={() => setActiveModal(null)}
        onSave={handleSaveWorld}
        currentId={activeWorldId}
        defaultName={worldMetadata.name || "My Custom World"}
      />

      {/* Spatial Topology CAD Designer Modal */}
      <TopologyDesignerModal
        opened={activeModal === "topology"}
        onClose={() => setActiveModal(null)}
        onSaveTopology={(newTopo: TopologyDefinition) =>
          simActor.send({ type: "SAVE_TOPOLOGY", topology: newTopo })
        }
        existingTopologies={topologies}
      />

      {/* State Schema & Domain Designer Modal */}
      <StateSchemaDesignerModal
        opened={activeModal === "schema"}
        onClose={() => setActiveModal(null)}
        stateDefinitions={stateDefinitions}
        onSaveStateDefinitions={(newStates: StateRowDefinition[]) =>
          simActor.send({ type: "SAVE_STATE_DEFINITIONS", stateDefinitions: newStates })
        }
        availableTopologies={topologies}
      />

      {/* Player HUD & Scoreboard Configurator Modal */}
      <WorldHudDesignerModal
        opened={activeModal === "hud"}
        onClose={() => setActiveModal(null)}
        stateVariables={stateDefinitions.map((s) => s.name)}
        onSaveHudConfig={(config: WorldHudConfig) => {
          try {
            const currentWorld = JSON.parse(worldJsonText);
            const updatedWorld = {
              ...currentWorld,
              metadata: {
                ...currentWorld.metadata,
                hud: config,
              },
            };
            simActor.send({
              type: "LOAD_WORLD",
              worldJsonText: JSON.stringify(updatedWorld, null, 2),
            });
            simActor.send({ type: "SET_DIRTY", isDirty: true });
          } catch (err) {
            console.error("Failed to save HUD config", err);
          }
        }}
      />
    </Box>
  );
};

export const WorldStudio: React.FC = () => {
  return (
    <SimulationContext.Provider>
      <WorldStudioInner />
    </SimulationContext.Provider>
  );
};

export default WorldStudio;
