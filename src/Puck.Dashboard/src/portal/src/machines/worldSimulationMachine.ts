import { setup, assign } from "xstate";
import { TopologyDefinition, WorldRule, getTopologyCoordinates } from "../engine/evaluator";
import { executePreviewAction, executeSimulationTick, StateDelta } from "../engine/tickRunner";
import { inspectWorldDocument } from "../engine/documentValidation";
import { TickSnapshot } from "../engine/replayTape";
import { StateRowDefinition } from "../components/world/StateMatrixView";
import { TIC_TAC_TOE_WORLD } from "../catalog/worldCatalog";
export interface SpeculativeResult {
  illegal: boolean;
  message?: string;
  cell?: number;
  nextWinner?: number;
  firedRuleName?: string;
  nextPlayer?: number;
  isWinningMove?: boolean;
}
export interface RayBeam {
  name: string;
  cells: number[];
}
export interface WorldSimulationContext {
  // World AST & Schema Declarations
  worldJsonText: string;
  parsedWorld: any;
  topologies: TopologyDefinition[];
  topologyMap: Record<string, TopologyDefinition>;
  selectedTopologyName: string;
  stateDefinitions: StateRowDefinition[];
  rules: WorldRule[];
  // Pure Immutable History Tape
  history: TickSnapshot[];
  historyIndex: number;
  // Transient Probing & Projections
  hoveredCell: number | null;
  hoveredMask: bigint | null;
  speculativeResult: SpeculativeResult | null;
  probedRayBeam: RayBeam | null;
  winningRayBeam: RayBeam | null;
  previewIssues: string[];
  error: string | null;
  // Local document state
  isDirty: boolean;
}
export type WorldSimulationEvent = {
  type: "LOAD_WORLD";
  worldJsonText: string;
} | {
  type: "SELECT_TOPOLOGY";
  name: string;
} | {
  type: "CELL_CLICK";
  cellIdx: number;
} | {
  type: "DISPATCH_ACTION";
  intent: string;
  mutations: Record<string, any>;
} | {
  type: "STATE_CHANGE";
  stateName: string;
  newValue: any;
} | {
  type: "UNDO";
} | {
  type: "REDO";
} | {
  type: "JUMP_TO_TICK";
  tickIndex: number;
} | {
  type: "RESET_WORLD";
} | {
  type: "HOVER_CELL";
  cellIdx: number | null;
} | {
  type: "HOVER_MASK";
  mask: bigint | null;
} | {
  type: "PROBE_RAY";
  ray: RayBeam | null;
} | {
  type: "ADD_RULE";
  rule: WorldRule;
} | {
  type: "SAVE_TOPOLOGY";
  topology: TopologyDefinition;
} | {
  type: "SAVE_STATE_DEFINITIONS";
  stateDefinitions: StateRowDefinition[];
} | {
  type: "SET_DIRTY";
  isDirty: boolean;
};
function createInitialSnapshot(stateDefinitions: StateRowDefinition[]): TickSnapshot {
  const initialScalars: Record<string, any> = {};
  const initialCells: Record<string, Record<number, number>> = {};
  stateDefinitions.forEach((s) => {
    if(s.domain) {
      initialCells[s.name] = {};
      if((s as any).cells && Array.isArray((s as any).cells)) {
        (s as any).cells.forEach((c: any) => {
          initialCells[s.name][Number(c.key)] = Number(c.value);
        });
      }
    }
    else {
      initialScalars[s.name] = s.value ?? 0;
    }
  });
  return {
    tickNumber: 0,
    state: initialScalars,
    boardCells: initialCells,
    trace: {
      tick: 0,
      intentDescription: "Initial World Initialization",
      ruleEvents: [],
      allDeltas: [],
    },
  };
}
const transient = { hoveredCell: null, hoveredMask: null, speculativeResult: null, winningRayBeam: null, probedRayBeam: null };
function load(text: string): WorldSimulationContext {
  const { world, previewIssues } = inspectWorldDocument(text);
  const topologies: TopologyDefinition[] = world.state.lattices ?? [];
  const stateDefinitions = world.state.world ?? [];
  return {
    worldJsonText: text, parsedWorld: world, previewIssues, error: null, topologies,
    topologyMap: Object.fromEntries(topologies.map(t => [t.name, t])),
    selectedTopologyName: topologies[0]?.name ?? "", stateDefinitions, rules: world.rules ?? [],
    history: [createInitialSnapshot(stateDefinitions)], historyIndex: 0, ...transient, isDirty: false
  };
}
function replaceDocument(context: WorldSimulationContext, world: any) {
  try {
    return { ...load(JSON.stringify(world, null, 2)), isDirty: true };
  }
  catch(error) {
    return { error: (error as Error).message, isDirty: context.isDirty };
  }
}
function step(context: WorldSimulationContext, mutations: Record<string, any>, intent: string, input = false) {
  try {
    if(context.previewIssues.length)
      throw new Error("Preview unavailable: " + context.previewIssues[0]);
    const snap = context.history[context.historyIndex];
    for(const key of Object.keys(mutations))
      if(!Object.hasOwn(snap.state, key))
        throw new Error("Unknown preview input: " + key);
    const run = input ? executePreviewAction : executeSimulationTick;
    const result = run(snap.tickNumber + 1, intent, mutations, snap.state, snap.boardCells, context.rules, context.topologyMap, snap.edgeLatches);
    for(const row of context.stateDefinitions.filter(row => !row.domain)) {
      const value = BigInt(result.nextState[row.name]);
      if((row.min !== undefined && value < BigInt(row.min)) || (row.max !== undefined && value > BigInt(row.max)) || (row.nonNegative && value < 0n))
        throw new Error("Preview value outside declared bounds: " + row.name);
    }
    const next: TickSnapshot = {
      tickNumber: result.trace.tick, state: result.nextState,
      boardCells: result.nextBoardCells, edgeLatches: result.nextEdgeLatches, trace: result.trace
    };
    const history = [...context.history.slice(0, context.historyIndex + 1), next].slice(-128);
    return { history, historyIndex: history.length - 1, error: null, ...transient };
  }
  catch(error) {
    return { error: (error as Error).message };
  }
}
export const worldSimulationMachine = setup({
  types: { context: {} as WorldSimulationContext, events: {} as WorldSimulationEvent },
  actions: {
    reduce: assign(({ context: c, event: e }) => {
      switch(e.type) {
        case "LOAD_WORLD": try {
          return load(e.worldJsonText);
        }
          catch(error) {
            return { error: (error as Error).message };
          }
        case "SELECT_TOPOLOGY": return { selectedTopologyName: e.name, ...transient };
        case "SET_DIRTY": return { isDirty: e.isDirty };
        case "HOVER_CELL": return {}; // Cell inspection is local UI state and never evaluates rules.
        case "HOVER_MASK": return { hoveredMask: e.mask };
        case "PROBE_RAY": return { probedRayBeam: e.ray };
        case "UNDO": return { historyIndex: Math.max(0, c.historyIndex - 1), error: null, ...transient };
        case "REDO": return { historyIndex: Math.min(c.history.length - 1, c.historyIndex + 1), error: null, ...transient };
        case "JUMP_TO_TICK": return { historyIndex: Math.max(0, Math.min(c.history.length - 1, e.tickIndex)), error: null, ...transient };
        case "RESET_WORLD": return { history: [createInitialSnapshot(c.stateDefinitions)], historyIndex: 0, error: null, ...transient };
        case "STATE_CHANGE": return step(c, { [e.stateName]: e.newValue }, "Edit preview register " + e.stateName);
        case "DISPATCH_ACTION": return step(c, e.mutations, e.intent);
        case "CELL_CLICK": {
          const snap = c.history[c.historyIndex];
          const prefix = Object.hasOwn(snap.state, "tttMoveRequest") ? "ttt" : Object.hasOwn(snap.state, "hexMoveRequest") ? "hex" : null;
          if(!prefix)
            return { error: "This document has no supported board input adapter. Inspect cells or edit preview registers." };
          const topology = c.topologyMap[c.selectedTopologyName];
          if(!topology || e.cellIdx < 0 || e.cellIdx >= getTopologyCoordinates(topology).length)
            return {};
          const board = c.stateDefinitions.find(s => s.domain?.topology === c.selectedTopologyName);
          if(!board || (snap.boardCells[board.name]?.[e.cellIdx] ?? 0) !== 0 || (snap.state[prefix + "Winner"] ?? 0) !== 0)
            return {};
          return step(c, { [prefix + "MoveCell"]: e.cellIdx, [prefix + "MoveRequest"]: Number(snap.state[prefix + "MoveRequest"]) + 1 }, "Place mark at cell " + e.cellIdx, true);
        }
        case "ADD_RULE": return replaceDocument(c, { ...c.parsedWorld, rules: [...c.rules, e.rule] });
        case "SAVE_STATE_DEFINITIONS": return replaceDocument(c, { ...c.parsedWorld, state: { ...c.parsedWorld.state, world: e.stateDefinitions } });
        case "SAVE_TOPOLOGY": {
          const lattices = c.topologies.some(t => t.name === e.topology.name) ? c.topologies.map(t => t.name === e.topology.name ? e.topology : t) : [...c.topologies, e.topology];
          return replaceDocument(c, { ...c.parsedWorld, state: { ...c.parsedWorld.state, lattices } });
        }
      }
    }),
  },
}).createMachine({
  id: "worldSimulation", context: () => load(JSON.stringify(TIC_TAC_TOE_WORLD, null, 2)),
  on: Object.fromEntries(["LOAD_WORLD", "SELECT_TOPOLOGY", "SET_DIRTY", "HOVER_CELL", "HOVER_MASK", "PROBE_RAY", "UNDO", "REDO", "JUMP_TO_TICK", "RESET_WORLD", "STATE_CHANGE", "DISPATCH_ACTION", "CELL_CLICK", "ADD_RULE", "SAVE_STATE_DEFINITIONS", "SAVE_TOPOLOGY"].map(type => [type, { actions: "reduce" as const }]))
});
// Fine-grained Reactive Selectors for React 19 / @xstate/react
export const selectCurrentSnapshot = (s: {
  context: WorldSimulationContext;
}): TickSnapshot | null => s.context.history[s.context.historyIndex] ?? null;
export const selectActivePlayer = (s: {
  context: WorldSimulationContext;
}): number => {
  const snap = s.context.history[s.context.historyIndex];
  return snap?.state["tttActive"] ?? snap?.state["hexTurn"] ?? snap?.state["chessTurn"] ?? 1;
};
export const selectWinner = (s: {
  context: WorldSimulationContext;
}): number => {
  const snap = s.context.history[s.context.historyIndex];
  return snap?.state["tttWinner"] ?? snap?.state["hexWinner"] ?? 0;
};
export const selectTickCount = (s: {
  context: WorldSimulationContext;
}): number => s.context.history[s.context.historyIndex]?.tickNumber ?? 0;
export const selectCurrentTickIndex = (s: {
  context: WorldSimulationContext;
}): number => s.context.historyIndex;
export const selectSnapshotsList = (s: {
  context: WorldSimulationContext;
}): TickSnapshot[] => s.context.history;
export const selectLiveState = (s: {
  context: WorldSimulationContext;
}): Record<string, any> => s.context.history[s.context.historyIndex]?.state ?? {};
export const selectLiveCells = (s: {
  context: WorldSimulationContext;
}): Record<number, number> => {
  const snap = s.context.history[s.context.historyIndex];
  return (snap?.boardCells?.[s.context.stateDefinitions.find(row => row.domain?.topology === s.context.selectedTopologyName)?.name ?? ""] ??
    {});
};
export const selectTopologies = (s: {
  context: WorldSimulationContext;
}): TopologyDefinition[] => s.context.topologies;
export const selectTopologyMap = (s: {
  context: WorldSimulationContext;
}): Record<string, TopologyDefinition> => s.context.topologyMap;
export const selectSelectedTopologyName = (s: {
  context: WorldSimulationContext;
}): string => s.context.selectedTopologyName;
export const selectActiveTopology = (s: {
  context: WorldSimulationContext;
}): TopologyDefinition | undefined => s.context.topologies.find((t) => t.name === s.context.selectedTopologyName) ??
  s.context.topologies[0];
export const selectStateDefinitions = (s: {
  context: WorldSimulationContext;
}): StateRowDefinition[] => s.context.stateDefinitions;
export const selectRules = (s: {
  context: WorldSimulationContext;
}): WorldRule[] => s.context.rules;
export const selectWorldJsonText = (s: {
  context: WorldSimulationContext;
}): string => s.context.worldJsonText;
export const selectIsDirty = (s: {
  context: WorldSimulationContext;
}): boolean => s.context.isDirty;
export const selectHoveredCell = (s: {
  context: WorldSimulationContext;
}): number | null => s.context.hoveredCell;
export const selectHoveredMask = (s: {
  context: WorldSimulationContext;
}): bigint | null => s.context.hoveredMask;
export const selectProbedRayBeam = (s: {
  context: WorldSimulationContext;
}): RayBeam | null => s.context.probedRayBeam;
export const selectWinningRayBeam = (s: {
  context: WorldSimulationContext;
}): RayBeam | null => s.context.winningRayBeam;
export const selectSpeculativeResult = (s: {
  context: WorldSimulationContext;
}): SpeculativeResult | null => s.context.speculativeResult;
export const selectDisplayedRayBeam = (s: {
  context: WorldSimulationContext;
}): RayBeam | null => s.context.winningRayBeam ?? s.context.probedRayBeam;
export const selectCanUndo = (s: {
  context: WorldSimulationContext;
}): boolean => s.context.historyIndex > 0;
export const selectCanRedo = (s: {
  context: WorldSimulationContext;
}): boolean => s.context.historyIndex < s.context.history.length - 1;
export const selectLastDeltas = (s: {
  context: WorldSimulationContext;
}): StateDelta[] => s.context.history[s.context.historyIndex]?.trace?.allDeltas ?? [];
