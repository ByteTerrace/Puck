import { StudioConfirmation, useStudioConfirmation } from "./StudioConfirmation";
import { AVAILABLE_PRESETS } from "../../catalog/worldCatalog";
import React, { useState, useEffect, useCallback } from "react";
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
import { TopologyDefinition, WorldRule, getTopologyCoordinates } from "../../engine/evaluator";
import { StateRowDefinition } from "./StateMatrixView";

const WorldStudioInner: React.FC = () => {
  const confirm = useStudioConfirmation();
  const [uiMessage, setUiMessage] = useState<string | null>(null);
  const simActor = SimulationContext.useActorRef();

  const selectCell = useCallback((cellIdx: number) => simActor.send({ type: "CELL_CLICK", cellIdx }), [simActor]);
  const changeState = useCallback((stateName: string, newValue: any) => simActor.send({ type: "STATE_CHANGE", stateName, newValue }), [simActor]);
  const resetState = useCallback(() => simActor.send({ type: "RESET_WORLD" }), [simActor]);
  const highlightMask = useCallback((mask: bigint | null) => simActor.send({ type: "HOVER_MASK", mask }), [simActor]);

  // Select immutable document and preview slices.
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
  const hoveredMask = SimulationContext.useSelector(selectHoveredMask);
  const displayedRayBeam = SimulationContext.useSelector(selectDisplayedRayBeam);
  const lastDeltas = SimulationContext.useSelector(selectLastDeltas);

  const [draft, setDraft] = useState(worldJsonText);
  useEffect(() => setDraft(worldJsonText), [worldJsonText]);
  const dirty = SimulationContext.useSelector(s => s.context.isDirty);
  const hasEdits = dirty || draft !== worldJsonText;
  const previewIssues = SimulationContext.useSelector(s => s.context.previewIssues);
  useEffect(() => {
    const guard = (event: BeforeUnloadEvent) => { if(hasEdits) { event.preventDefault(); event.returnValue = ""; } };
    const navigateGuard = (event: Event) => { if(hasEdits) { event.preventDefault(); void confirm("Discard unsaved document edits and leave the studio?").then(accepted => { if(accepted) (event as CustomEvent).detail?.continueNavigation(); }); } };
    window.addEventListener("puck-before-navigate", navigateGuard);
    window.addEventListener("beforeunload", guard); return () => { window.removeEventListener("beforeunload", guard); window.removeEventListener("puck-before-navigate", navigateGuard); };
  }, [hasEdits, confirm]);

  // Studio UI Modals (discriminated union prevents conflicting open modals)
  const [activeModal, setActiveModal] = useState<StudioModalType>(null);
  const [selectedPresetId, setSelectedPresetId] = useState<string>("tictactoe");

  // Local document metadata
  const [activeWorldId, setActiveWorldId] = useState<string>("tictactoe");
  const [worldMetadata, setWorldMetadata] = useState<Partial<WorldMetadata>>({
    name: "Qubic 3D 4x4x4 Tic-Tac-Toe",
    author: "ByteTerrace",
    description: "Standard 3D Qubic on a 4x4x4 lattice substrate.",
    category: "Strategy",
    visibility: "public",
  });

  const handleSelectPreset = async (presetId: string, metadata: Partial<WorldMetadata>) => {
    if(hasEdits && !await confirm("Discard unsaved document edits and open this example?")) return;
    const preset = AVAILABLE_PRESETS.find(p => p.id === presetId);
    if(!preset) return;
    simActor.send({ type: "LOAD_WORLD", worldJsonText: JSON.stringify(preset.world, null, 2) });
    setDraft(simActor.getSnapshot().context.worldJsonText);
    setSelectedPresetId(presetId);
    setActiveWorldId(presetId);
    setWorldMetadata(metadata);
  };

  const handleSaveWorld = async (id: string, meta: Partial<WorldMetadata>) => {
    if(draft !== worldJsonText) throw new Error("Apply your JSON edits before saving.");
    const parsed = JSON.parse(worldJsonText);
    await defaultWorldStorageClient.saveWorld(id, parsed, meta);
    setActiveWorldId(id);
    setWorldMetadata((prev) => ({ ...prev, ...meta, name: meta.name || prev.name }));
    if(simActor.getSnapshot().context.worldJsonText === worldJsonText) simActor.send({ type: "SET_DIRTY", isDirty: false });
  };

  const handleLoadWorld = async (worldData: any, meta: any) => {
    if(hasEdits && !await confirm("Discard unsaved document edits and load this world?")) return;
    const json = JSON.stringify(worldData, null, 2);
    simActor.send({ type: "LOAD_WORLD", worldJsonText: json });
    if(simActor.getSnapshot().context.error) return;
    setDraft(json);
    if(meta?.id) {
      setActiveWorldId(meta.id);
      setSelectedPresetId(meta.id);
    }
    if(meta) setWorldMetadata(meta);
  };

  return (
    <Box
      p="md"
      className="world-studio">
      {/* Studio Header Toolbar & Controls */}
      <WorldStudioHeader
        selectedPresetId={selectedPresetId}
        draftDirty={draft !== worldJsonText}
        onSelectPreset={handleSelectPreset}
        onOpenModal={modal => { if(draft !== worldJsonText && ["rule", "schema", "topology", "hud"].includes(modal ?? "")) { setUiMessage("Apply or export your JSON draft before opening a designer."); return; } setUiMessage(null); setActiveModal(modal); }}
      />

      {/* Stable preview status */}
      <WorldStudioAlerts />
      {uiMessage && <Text
        role="status"
        size="sm"
        mb="sm">{uiMessage}</Text>}

      {/* Main Studio Two-Column Grid */}
      <Grid
        gap="md">
        {/* Left Column: Topology and preview registers (7 Cols) */}
        <Grid.Col
          span={{ base: 12, md: 7 }}>
          <Stack
            gap="md">
            {activeTopology ? (
              <UniversalTopologyView
                key={activeTopology.name + worldJsonText}
                topology={activeTopology}
                cellValues={liveCells}
                selectedCell={liveState["tttMoveCell"]}
                onSelectCell={selectCell}
                hoveredMask={hoveredMask}
                activeWinningRay={displayedRayBeam}
              />
            ) : (
              <Text
                c="var(--ink-faint)"
                size="sm">
                No topology declared in this world.
              </Text>
            )}

            <StateMatrixView
              stateDefinitions={stateDefinitions}
              currentState={liveState}
              lastDeltas={lastDeltas}
              onStateChange={changeState}
              onResetState={resetState}
              onHoverMask={highlightMask}
            />
          </Stack>
        </Grid.Col>

        {/* Right Column: Advanced Simulation Subsystem Tabs (5 Cols) */}
        <Grid.Col
          span={{ base: 12, md: 5 }}>
          <Tabs
            defaultValue="workbench"
            variant="outline"
            keepMounted={false}>
            <Tabs.List
              mb="xs"
              style={{ flexWrap: "wrap" }}>
              <Tabs.Tab
                value="trace"
                leftSection={<RiHistoryLine
                  size={15} />}>
                Trace Log
              </Tabs.Tab>
              <Tabs.Tab
                disabled={previewIssues.length > 0}
                value="scenarios"
                leftSection={<RiFlaskLine
                  size={15} />}>
                Test Suite
              </Tabs.Tab>
              <Tabs.Tab
                disabled={previewIssues.length > 0}
                value="montecarlo"
                leftSection={<RiCpuLine
                  size={15} />}>
                Monte Carlo
              </Tabs.Tab>
              <Tabs.Tab
                disabled={previewIssues.length > 0 || (activeTopology ? getTopologyCoordinates(activeTopology).length > 64 : true)}
                value="rays"
                leftSection={<RiCompass3Fill
                  size={15} />}>
                Ray Prober
              </Tabs.Tab>
              <Tabs.Tab
                value="repl"
                leftSection={<RiTerminalBoxLine
                  size={15} />}>
                REPL
              </Tabs.Tab>
              <Tabs.Tab
                value="rules"
                leftSection={<RiGitMergeLine
                  size={15} />}>
                Rule DAG
              </Tabs.Tab>
              <Tabs.Tab
                value="workbench"
                leftSection={<RiCodeSSlashLine
                  size={15} />}>
                JSON
              </Tabs.Tab>
            </Tabs.List>

            {/* Tab 1: Causal Execution Trace with Predicate Truth Trees */}
            <Tabs.Panel
              value="trace">
              <ExecutionTraceLog
                snapshots={snapshotsList}
                currentTickIndex={currentTickIndex}
                onJumpToTick={(tickIndex) => simActor.send({ type: "JUMP_TO_TICK", tickIndex })}
              />
            </Tabs.Panel>

            {/* Tab 2: Scenario Test Suite */}
            <Tabs.Panel
              value="scenarios">
              <ScenarioTestStudio
                key={worldJsonText}
                initial={snapshotsList[0]}
                worldId={selectedPresetId}
                rules={rules}
                topologies={topologyMap}
              />
            </Tabs.Panel>

            {/* Tab 3: Monte Carlo Batch Rollout & Rule Health */}
            <Tabs.Panel
              value="montecarlo">
              <MonteCarloStudio
                key={worldJsonText + ":" + currentTickIndex}
                rules={rules}
                topologies={topologyMap}
                initialState={currentSnapshot?.state ?? {}}
                initialBoardCells={currentSnapshot?.boardCells ?? {}}
              />
            </Tabs.Panel>

            {/* Tab 4: Lattice ray inspector */}
            <Tabs.Panel
              value="rays">
              {activeTopology ? (
                <LatticeRayStudio
                  topology={activeTopology}
                  onSelectRayBeam={(rayName, cells) =>
                    simActor.send({ type: "PROBE_RAY", ray: { name: rayName, cells } })
                  }
                  activeRayName={displayedRayBeam?.name}
                />
              ) : (
                <Text
                  size="xs"
                  c="dimmed">
                  No active topology to probe.
                </Text>
              )}
            </Tabs.Panel>

            {/* Tab 5: Interactive Expression REPL */}
            <Tabs.Panel
              value="repl">
              <PuckReplConsole
                state={liveState}
                boardCells={currentSnapshot?.boardCells ?? {}}
                topologies={topologyMap}
              />
            </Tabs.Panel>

            {/* Tab 6: Reactive Rule Dependency DAG */}
            <Tabs.Panel
              value="rules">
              <ReactiveRuleGraph
                rules={rules}
                currentState={liveState}
                boardCells={currentSnapshot?.boardCells} />
            </Tabs.Panel>

            {/* Tab 7: Raw World JSON Workbench */}
            <Tabs.Panel
              value="workbench">
              <WorldWorkbench
                worldJson={worldJsonText}
                draft={draft}
                onDraftChange={setDraft}
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
      {activeModal === "rule" && (
        <RuleAuthoringModal
          opened={activeModal === "rule"}
          onClose={() => setActiveModal(null)}
          onSaveRule={(newRule: WorldRule) => simActor.send({ type: "ADD_RULE", rule: newRule })}
          existingStateVars={stateDefinitions.map((s) => s.name)}
          topologyNames={topologies.map((t) => t.name)}
        />
      )}

      {/* Local document library and saved revisions */}
      {activeModal === "vault" && (
        <WorldVaultModal
          opened={activeModal === "vault"}
          onClose={() => setActiveModal(null)}
          storageClient={defaultWorldStorageClient}
          activeWorldId={activeWorldId}
          onLoadWorld={handleLoadWorld}
          onOpenSaveModal={() => setActiveModal("save")}
        />
      )}

      {/* Save World Modal */}
      {activeModal === "save" && (
        <SaveWorldModal
          opened={activeModal === "save"}
          onClose={() => setActiveModal(null)}
          onSave={handleSaveWorld}
          currentId={AVAILABLE_PRESETS.some(p => p.id === activeWorldId) ? activeWorldId + "-copy" : activeWorldId}
          defaultName={simActor.getSnapshot().context.parsedWorld.metadata?.title || worldMetadata.name || "My Custom World"}
        />
      )}

      {/* Spatial Topology CAD Designer Modal */}
      {activeModal === "topology" && (
        <TopologyDesignerModal
          opened={activeModal === "topology"}
          onClose={() => setActiveModal(null)}
          onSaveTopology={(newTopo: TopologyDefinition) =>
            simActor.send({ type: "SAVE_TOPOLOGY", topology: newTopo })
          }
          existingTopologies={topologies}
        />
      )}

      {/* State Schema & Domain Designer Modal */}
      {activeModal === "schema" && (
        <StateSchemaDesignerModal
          opened={activeModal === "schema"}
          onClose={() => setActiveModal(null)}
          stateDefinitions={stateDefinitions}
          onSaveStateDefinitions={(newStates: StateRowDefinition[]) =>
            simActor.send({ type: "SAVE_STATE_DEFINITIONS", stateDefinitions: newStates })
          }
          availableTopologies={topologies}
        />
      )}

      {/* Player HUD & Scoreboard Configurator Modal */}
      {activeModal === "hud" && (
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
                  custom: { ...currentWorld.metadata?.custom, puckStudioHud: JSON.stringify(config) },
                },
              };
              simActor.send({
                type: "LOAD_WORLD",
                worldJsonText: JSON.stringify(updatedWorld, null, 2),
              });
              simActor.send({ type: "SET_DIRTY", isDirty: true });
            } catch(err) {
              console.error("Failed to save HUD config", err);
            }
          }}
        />
      )}
    </Box>
  );
};

export const WorldStudio: React.FC = () => {
  return (
    <SimulationContext.Provider>
      <StudioConfirmation><WorldStudioInner /></StudioConfirmation>
    </SimulationContext.Provider>
  );
};

export default WorldStudio;
