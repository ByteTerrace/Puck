import { setup, assign } from "xstate";
import {
  TopologyDefinition,
  WorldRule,
  boardShift,
  computeBoardMask,
  getTopologyCoordinates,
} from "../engine/evaluator";
import { executeSimulationTick, StateDelta } from "../engine/tickRunner";
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

  // Cloud Sync State
  isDirty: boolean;
}

export type WorldSimulationEvent =
  | { type: "LOAD_WORLD"; worldJsonText: string }
  | { type: "SELECT_TOPOLOGY"; name: string }
  | { type: "CELL_CLICK"; cellIdx: number }
  | { type: "DISPATCH_ACTION"; intent: string; mutations: Record<string, any> }
  | { type: "STATE_CHANGE"; stateName: string; newValue: any }
  | { type: "UNDO" }
  | { type: "REDO" }
  | { type: "JUMP_TO_TICK"; tickIndex: number }
  | { type: "RESET_WORLD" }
  | { type: "HOVER_CELL"; cellIdx: number | null }
  | { type: "HOVER_MASK"; mask: bigint | null }
  | { type: "PROBE_RAY"; ray: RayBeam | null }
  | { type: "ADD_RULE"; rule: WorldRule }
  | { type: "SAVE_TOPOLOGY"; topology: TopologyDefinition }
  | { type: "SAVE_STATE_DEFINITIONS"; stateDefinitions: StateRowDefinition[] }
  | { type: "SET_DIRTY"; isDirty: boolean };

function parseWorldData(worldJsonText: string) {
  try {
    return JSON.parse(worldJsonText);
  } catch {
    return TIC_TAC_TOE_WORLD;
  }
}

function createInitialSnapshot(stateDefinitions: StateRowDefinition[]): TickSnapshot {
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

function computeWinningRay(
  winner: number,
  liveCells: Record<number, number>,
  activeTopology?: TopologyDefinition
): RayBeam | null {
  if (!winner || winner === 3 || !activeTopology) return null;

  const playerVal = winner; // 1 for X, 2 for O
  const mask = computeBoardMask(liveCells, playerVal);
  const coords = getTopologyCoordinates(activeTopology);

  for (const dir of activeTopology.directions ?? []) {
    const s1 = boardShift(mask, activeTopology, dir.name);
    const s2 = boardShift(s1 & mask, activeTopology, dir.name);
    const s3 = boardShift(s2 & mask, activeTopology, dir.name);
    const winBits = s3 & mask;

    if (winBits !== 0n) {
      const winningIndices: number[] = [];
      for (let i = 0; i < coords.length; i++) {
        if ((winBits & (1n << BigInt(i))) !== 0n) {
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
}

function computeSpeculativeGhost(
  cellIdx: number | null,
  context: WorldSimulationContext
): SpeculativeResult | null {
  if (cellIdx === null) return null;

  const currentSnapshot = context.history[context.historyIndex];
  if (!currentSnapshot) return null;

  const liveCells =
    currentSnapshot.boardCells?.["tttBoard"] ??
    currentSnapshot.boardCells?.[context.stateDefinitions[0]?.name ?? ""] ??
    {};
  const liveState = currentSnapshot.state ?? {};

  const isOccupied = liveCells[cellIdx] && liveCells[cellIdx] !== 0;
  if (isOccupied) {
    return { illegal: true, message: `Cell #${cellIdx} is already occupied` };
  }

  const moveRequestSeq = Number(liveState["tttMoveRequest"] ?? 0) + 1;
  const sim = executeSimulationTick(
    (currentSnapshot.tickNumber ?? 0) + 1,
    `Speculative Move at #${cellIdx}`,
    {
      tttMoveCell: cellIdx,
      tttMoveRequest: moveRequestSeq,
      hexMoveCell: cellIdx,
      hexMoveRequest: moveRequestSeq,
    },
    liveState,
    currentSnapshot.boardCells,
    context.rules,
    context.topologyMap
  );

  const nextWinner = sim.nextState["tttWinner"] ?? sim.nextState["hexWinner"] ?? 0;
  const firedRule = sim.trace.ruleEvents.find((r) => r.fired);
  const nextPlayer = sim.nextState["tttActive"] ?? sim.nextState["hexTurn"] ?? 1;

  return {
    illegal: false,
    cell: cellIdx,
    nextWinner,
    firedRuleName: firedRule?.ruleName ?? "ttt-place-mark",
    nextPlayer,
    isWinningMove: nextWinner > 0,
  };
}

const initialJson = JSON.stringify(TIC_TAC_TOE_WORLD, null, 2);
const initialParsed = parseWorldData(initialJson);
const initialTopologies: TopologyDefinition[] = initialParsed.state?.lattices ?? [];
const initialTopologyMap: Record<string, TopologyDefinition> = {};
initialTopologies.forEach((t) => {
  initialTopologyMap[t.name] = t;
});
const initialStateDefs: StateRowDefinition[] = initialParsed.state?.world ?? [];
const initialSnap = createInitialSnapshot(initialStateDefs);

export const worldSimulationMachine = setup({
  types: {
    context: {} as WorldSimulationContext,
    events: {} as WorldSimulationEvent,
  },
  guards: {
    canMakeMove: ({ context, event }) => {
      if (event.type !== "CELL_CLICK") return false;
      const currentSnapshot = context.history[context.historyIndex];
      if (!currentSnapshot) return false;
      const liveCells =
        currentSnapshot.boardCells?.["tttBoard"] ??
        currentSnapshot.boardCells?.[context.stateDefinitions[0]?.name ?? ""] ??
        {};
      // Check if cell is within bounds and unoccupied
      const isOccupied = liveCells[event.cellIdx] && liveCells[event.cellIdx] !== 0;
      return !isOccupied;
    },
    canUndo: ({ context }) => context.historyIndex > 0,
    canRedo: ({ context }) => context.historyIndex < context.history.length - 1,
    hasWinner: ({ context }) => {
      const snap = context.history[context.historyIndex];
      const win = snap?.state["tttWinner"] ?? snap?.state["hexWinner"] ?? 0;
      return win > 0 && win !== 3;
    },
    isDraw: ({ context }) => {
      const snap = context.history[context.historyIndex];
      const win = snap?.state["tttWinner"] ?? snap?.state["hexWinner"] ?? 0;
      return win === 3;
    },
  },
  actions: {
    loadWorld: assign(({ context, event }) => {
      if (event.type !== "LOAD_WORLD") return {};
      const parsed = parseWorldData(event.worldJsonText);
      const topologies: TopologyDefinition[] = parsed.state?.lattices ?? [];
      const topologyMap: Record<string, TopologyDefinition> = {};
      topologies.forEach((t) => {
        topologyMap[t.name] = t;
      });
      const stateDefinitions: StateRowDefinition[] = parsed.state?.world ?? [];
      const rules: WorldRule[] = parsed.rules ?? [];
      const initialSnapshot = createInitialSnapshot(stateDefinitions);

      return {
        worldJsonText: event.worldJsonText,
        parsedWorld: parsed,
        topologies,
        topologyMap,
        selectedTopologyName: topologies[0]?.name ?? context.selectedTopologyName,
        stateDefinitions,
        rules,
        history: [initialSnapshot],
        historyIndex: 0,
        hoveredCell: null,
        speculativeResult: null,
        winningRayBeam: null,
        probedRayBeam: null,
        isDirty: false,
      };
    }),

    selectTopology: assign(({ event }) => {
      if (event.type !== "SELECT_TOPOLOGY") return {};
      return { selectedTopologyName: event.name };
    }),

    hoverCell: assign(({ context, event }) => {
      if (event.type !== "HOVER_CELL") return {};
      const spec = computeSpeculativeGhost(event.cellIdx, context);
      return {
        hoveredCell: event.cellIdx,
        speculativeResult: spec,
      };
    }),

    clearHover: assign({
      hoveredCell: () => null,
      speculativeResult: () => null,
    }),

    hoverMask: assign(({ event }) => {
      if (event.type !== "HOVER_MASK") return {};
      return { hoveredMask: event.mask };
    }),

    probeRay: assign(({ event }) => {
      if (event.type !== "PROBE_RAY") return {};
      return { probedRayBeam: event.ray };
    }),

    setDirty: assign(({ event }) => {
      if (event.type !== "SET_DIRTY") return {};
      return { isDirty: event.isDirty };
    }),

    executeCellMove: assign(({ context, event }) => {
      if (event.type !== "CELL_CLICK") return {};
      const currentSnapshot = context.history[context.historyIndex];
      if (!currentSnapshot) return {};

      const liveState = currentSnapshot.state ?? {};
      const moveRequestSeq = Number(liveState["tttMoveRequest"] ?? 0) + 1;
      const nextTick = (currentSnapshot.tickNumber ?? 0) + 1;

      const mutations: Record<string, any> = {
        tttMoveCell: event.cellIdx,
        tttMoveRequest: moveRequestSeq,
        hexMoveCell: event.cellIdx,
        hexMoveRequest: moveRequestSeq,
      };

      const result = executeSimulationTick(
        nextTick,
        `Place mark at Cell #${event.cellIdx}`,
        mutations,
        liveState,
        currentSnapshot.boardCells,
        context.rules,
        context.topologyMap
      );

      const nextSnapshot: TickSnapshot = {
        tickNumber: nextTick,
        state: result.nextState,
        boardCells: result.nextBoardCells,
        trace: result.trace,
      };

      // Truncate any forward history and append new step
      const newHistory = [
        ...context.history.slice(0, context.historyIndex + 1),
        nextSnapshot,
      ];

      return {
        history: newHistory,
        historyIndex: newHistory.length - 1,
        hoveredCell: null,
        speculativeResult: null,
      };
    }),

    dispatchCustomAction: assign(({ context, event }) => {
      if (event.type !== "DISPATCH_ACTION") return {};
      const currentSnapshot = context.history[context.historyIndex];
      if (!currentSnapshot) return {};

      const nextTick = (currentSnapshot.tickNumber ?? 0) + 1;
      const result = executeSimulationTick(
        nextTick,
        event.intent,
        event.mutations,
        currentSnapshot.state,
        currentSnapshot.boardCells,
        context.rules,
        context.topologyMap
      );

      const nextSnapshot: TickSnapshot = {
        tickNumber: nextTick,
        state: result.nextState,
        boardCells: result.nextBoardCells,
        trace: result.trace,
      };

      const newHistory = [
        ...context.history.slice(0, context.historyIndex + 1),
        nextSnapshot,
      ];

      return {
        history: newHistory,
        historyIndex: newHistory.length - 1,
      };
    }),

    mutateSingleState: assign(({ context, event }) => {
      if (event.type !== "STATE_CHANGE") return {};
      const currentSnapshot = context.history[context.historyIndex];
      if (!currentSnapshot) return {};

      const nextTick = (currentSnapshot.tickNumber ?? 0) + 1;
      const result = executeSimulationTick(
        nextTick,
        `Manual state tweak: ${event.stateName} = ${event.newValue}`,
        { [event.stateName]: event.newValue },
        currentSnapshot.state,
        currentSnapshot.boardCells,
        context.rules,
        context.topologyMap
      );

      const nextSnapshot: TickSnapshot = {
        tickNumber: nextTick,
        state: result.nextState,
        boardCells: result.nextBoardCells,
        trace: result.trace,
      };

      const newHistory = [
        ...context.history.slice(0, context.historyIndex + 1),
        nextSnapshot,
      ];

      return {
        history: newHistory,
        historyIndex: newHistory.length - 1,
      };
    }),

    undoStep: assign(({ context }) => {
      if (context.historyIndex <= 0) return {};
      return { historyIndex: context.historyIndex - 1 };
    }),

    redoStep: assign(({ context }) => {
      if (context.historyIndex >= context.history.length - 1) return {};
      return { historyIndex: context.historyIndex + 1 };
    }),

    jumpToStep: assign(({ context, event }) => {
      if (event.type !== "JUMP_TO_TICK") return {};
      if (event.tickIndex < 0 || event.tickIndex >= context.history.length) return {};
      return { historyIndex: event.tickIndex };
    }),

    resetSimulation: assign(({ context }) => {
      const initialSnapshot = createInitialSnapshot(context.stateDefinitions);
      return {
        history: [initialSnapshot],
        historyIndex: 0,
        winningRayBeam: null,
        hoveredCell: null,
        speculativeResult: null,
      };
    }),

    illuminateWinningRay: assign(({ context }) => {
      const snap = context.history[context.historyIndex];
      if (!snap) return {};
      const winner = snap.state["tttWinner"] ?? snap.state["hexWinner"] ?? 0;
      const activeTopo =
        context.topologies.find((t) => t.name === context.selectedTopologyName) ??
        context.topologies[0];
      const liveCells =
        snap.boardCells?.["tttBoard"] ??
        snap.boardCells?.[context.stateDefinitions[0]?.name ?? ""] ??
        {};
      const winningRay = computeWinningRay(winner, liveCells, activeTopo);
      return { winningRayBeam: winningRay };
    }),

    saveRule: assign(({ context, event }) => {
      if (event.type !== "ADD_RULE") return {};
      try {
        const currentWorld = JSON.parse(context.worldJsonText);
        const updatedRules = [...(currentWorld.rules ?? []), event.rule];
        const updatedWorld = { ...currentWorld, rules: updatedRules };
        const json = JSON.stringify(updatedWorld, null, 2);
        return {
          worldJsonText: json,
          parsedWorld: updatedWorld,
          rules: updatedRules,
          isDirty: true,
        };
      } catch {
        return {};
      }
    }),

    saveTopologyDef: assign(({ context, event }) => {
      if (event.type !== "SAVE_TOPOLOGY") return {};
      try {
        const currentWorld = JSON.parse(context.worldJsonText);
        const existingLattices: TopologyDefinition[] = currentWorld.state?.lattices ?? [];
        const idx = existingLattices.findIndex((t) => t.name === event.topology.name);
        let updatedLattices: TopologyDefinition[];
        if (idx >= 0) {
          updatedLattices = [...existingLattices];
          updatedLattices[idx] = event.topology;
        } else {
          updatedLattices = [...existingLattices, event.topology];
        }
        const updatedWorld = {
          ...currentWorld,
          state: {
            ...currentWorld.state,
            lattices: updatedLattices,
          },
        };
        const topologyMap: Record<string, TopologyDefinition> = {};
        updatedLattices.forEach((t) => {
          topologyMap[t.name] = t;
        });
        const json = JSON.stringify(updatedWorld, null, 2);
        return {
          worldJsonText: json,
          parsedWorld: updatedWorld,
          topologies: updatedLattices,
          topologyMap,
          selectedTopologyName: event.topology.name,
          isDirty: true,
        };
      } catch {
        return {};
      }
    }),

    saveStateDefs: assign(({ context, event }) => {
      if (event.type !== "SAVE_STATE_DEFINITIONS") return {};
      try {
        const currentWorld = JSON.parse(context.worldJsonText);
        const updatedWorld = {
          ...currentWorld,
          state: {
            ...currentWorld.state,
            world: event.stateDefinitions,
          },
        };
        const json = JSON.stringify(updatedWorld, null, 2);
        return {
          worldJsonText: json,
          parsedWorld: updatedWorld,
          stateDefinitions: event.stateDefinitions,
          isDirty: true,
        };
      } catch {
        return {};
      }
    }),
  },
}).createMachine({
  id: "worldSimulation",
  initial: "ready",
  context: {
    worldJsonText: initialJson,
    parsedWorld: initialParsed,
    topologies: initialTopologies,
    topologyMap: initialTopologyMap,
    selectedTopologyName: initialTopologies[0]?.name ?? "tttCube",
    stateDefinitions: initialStateDefs,
    rules: initialParsed.rules ?? [],
    history: [initialSnap],
    historyIndex: 0,
    hoveredCell: null,
    hoveredMask: null,
    probedRayBeam: null,
    speculativeResult: null,
    winningRayBeam: null,
    isDirty: false,
  },
  states: {
    ready: {
      initial: "idle",
      states: {
        idle: {
          on: {
            CELL_CLICK: {
              guard: "canMakeMove",
              target: "#worldSimulation.evaluating",
              actions: "executeCellMove",
            },
            HOVER_CELL: {
              target: "speculating",
              actions: "hoverCell",
            },
          },
        },
        speculating: {
          on: {
            CELL_CLICK: {
              guard: "canMakeMove",
              target: "#worldSimulation.evaluating",
              actions: "executeCellMove",
            },
            HOVER_CELL: {
              actions: "hoverCell",
            },
          },
        },
      },
      on: {
        DISPATCH_ACTION: {
          target: "evaluating",
          actions: "dispatchCustomAction",
        },
        STATE_CHANGE: {
          target: "evaluating",
          actions: "mutateSingleState",
        },
        UNDO: {
          guard: "canUndo",
          target: "scrubbing",
          actions: "undoStep",
        },
        REDO: {
          guard: "canRedo",
          target: "scrubbing",
          actions: "redoStep",
        },
        JUMP_TO_TICK: {
          target: "scrubbing",
          actions: "jumpToStep",
        },
        RESET_WORLD: {
          target: ".idle",
          actions: "resetSimulation",
        },
      },
    },

    evaluating: {
      always: [
        {
          guard: "hasWinner",
          target: "gameEnded.won",
        },
        {
          guard: "isDraw",
          target: "gameEnded.draw",
        },
        {
          target: "ready.idle",
        },
      ],
    },

    scrubbing: {
      always: [
        {
          guard: "hasWinner",
          target: "gameEnded.won",
        },
      ],
      on: {
        CELL_CLICK: {
          guard: "canMakeMove",
          target: "evaluating",
          actions: "executeCellMove",
        },
        UNDO: {
          guard: "canUndo",
          actions: "undoStep",
        },
        REDO: {
          guard: "canRedo",
          actions: "redoStep",
        },
        JUMP_TO_TICK: {
          actions: "jumpToStep",
        },
        RESET_WORLD: {
          target: "ready.idle",
          actions: "resetSimulation",
        },
      },
    },

    gameEnded: {
      initial: "won",
      states: {
        won: {
          entry: "illuminateWinningRay",
        },
        draw: {},
      },
      on: {
        RESET_WORLD: {
          target: "ready.idle",
          actions: "resetSimulation",
        },
        UNDO: {
          guard: "canUndo",
          target: "scrubbing",
          actions: "undoStep",
        },
        JUMP_TO_TICK: {
          target: "scrubbing",
          actions: "jumpToStep",
        },
      },
    },
  },
  on: {
    LOAD_WORLD: {
      target: ".ready.idle",
      actions: "loadWorld",
    },
    SELECT_TOPOLOGY: {
      actions: "selectTopology",
    },
    HOVER_MASK: {
      actions: "hoverMask",
    },
    PROBE_RAY: {
      actions: "probeRay",
    },
    ADD_RULE: {
      actions: "saveRule",
    },
    SAVE_TOPOLOGY: {
      actions: "saveTopologyDef",
    },
    SAVE_STATE_DEFINITIONS: {
      actions: "saveStateDefs",
    },
    SET_DIRTY: {
      actions: "setDirty",
    },
  },
});

// Fine-grained Reactive Selectors for React 19 / @xstate/react
export const selectCurrentSnapshot = (s: { context: WorldSimulationContext }): TickSnapshot | null =>
  s.context.history[s.context.historyIndex] ?? null;

export const selectActivePlayer = (s: { context: WorldSimulationContext }): number => {
  const snap = s.context.history[s.context.historyIndex];
  return snap?.state["tttActive"] ?? snap?.state["hexTurn"] ?? snap?.state["chessTurn"] ?? 1;
};

export const selectWinner = (s: { context: WorldSimulationContext }): number => {
  const snap = s.context.history[s.context.historyIndex];
  return snap?.state["tttWinner"] ?? snap?.state["hexWinner"] ?? 0;
};

export const selectTickCount = (s: { context: WorldSimulationContext }): number =>
  s.context.history[s.context.historyIndex]?.tickNumber ?? 0;

export const selectCurrentTickIndex = (s: { context: WorldSimulationContext }): number =>
  s.context.historyIndex;

export const selectSnapshotsList = (s: { context: WorldSimulationContext }): TickSnapshot[] =>
  s.context.history;

export const selectLiveState = (s: { context: WorldSimulationContext }): Record<string, any> =>
  s.context.history[s.context.historyIndex]?.state ?? {};

export const selectLiveCells = (s: { context: WorldSimulationContext }): Record<number, number> => {
  const snap = s.context.history[s.context.historyIndex];
  return (
    snap?.boardCells?.["tttBoard"] ??
    snap?.boardCells?.[s.context.stateDefinitions[0]?.name ?? ""] ??
    {}
  );
};

export const selectTopologies = (s: { context: WorldSimulationContext }): TopologyDefinition[] =>
  s.context.topologies;

export const selectTopologyMap = (s: { context: WorldSimulationContext }): Record<string, TopologyDefinition> =>
  s.context.topologyMap;

export const selectSelectedTopologyName = (s: { context: WorldSimulationContext }): string =>
  s.context.selectedTopologyName;

export const selectActiveTopology = (s: { context: WorldSimulationContext }): TopologyDefinition | undefined =>
  s.context.topologies.find((t) => t.name === s.context.selectedTopologyName) ??
  s.context.topologies[0];

export const selectStateDefinitions = (s: { context: WorldSimulationContext }): StateRowDefinition[] =>
  s.context.stateDefinitions;

export const selectRules = (s: { context: WorldSimulationContext }): WorldRule[] =>
  s.context.rules;

export const selectWorldJsonText = (s: { context: WorldSimulationContext }): string =>
  s.context.worldJsonText;

export const selectIsDirty = (s: { context: WorldSimulationContext }): boolean =>
  s.context.isDirty;

export const selectHoveredCell = (s: { context: WorldSimulationContext }): number | null =>
  s.context.hoveredCell;

export const selectHoveredMask = (s: { context: WorldSimulationContext }): bigint | null =>
  s.context.hoveredMask;

export const selectProbedRayBeam = (s: { context: WorldSimulationContext }): RayBeam | null =>
  s.context.probedRayBeam;

export const selectWinningRayBeam = (s: { context: WorldSimulationContext }): RayBeam | null =>
  s.context.winningRayBeam;

export const selectSpeculativeResult = (s: { context: WorldSimulationContext }): SpeculativeResult | null =>
  s.context.speculativeResult;

export const selectDisplayedRayBeam = (s: { context: WorldSimulationContext }): RayBeam | null =>
  s.context.winningRayBeam ?? s.context.probedRayBeam;

export const selectCanUndo = (s: { context: WorldSimulationContext }): boolean =>
  s.context.historyIndex > 0;

export const selectCanRedo = (s: { context: WorldSimulationContext }): boolean =>
  s.context.historyIndex < s.context.history.length - 1;

export const selectLastDeltas = (s: { context: WorldSimulationContext }): StateDelta[] =>
  s.context.history[s.context.historyIndex]?.trace?.allDeltas ?? [];
