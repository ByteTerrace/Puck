import { setup, assign } from "xstate";
import { TopologyDefinition, WorldRule } from "../engine/evaluator";
import { executePreviewAction, executeSimulationTick, StateDelta } from "../engine/tickRunner";
import { inspectWorldDocument } from "../engine/documentValidation";
import { TickSnapshot } from "../engine/replayTape";
import { paintCells, validSelection, type CellReference, type StateRowDefinition } from "../authoring/documentTools";
import { bindAppearance, type ValueAppearance } from "../authoring/presentation";
import { previewCellAction } from "../engine/previewInputAdapter";
import { TIC_TAC_TOE_WORLD } from "../catalog/worldCatalog";

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
  hoveredMask: bigint | null;
  probedRayBeam: RayBeam | null;
  previewIssues: string[];
  error: string | null;
  // Local document state
  documentHistory: { text: string; label: string }[];
  documentIndex: number;
  savedDocumentText: string;
  selection: CellReference[];
  isDirty: boolean;
}
export type WorldSimulationEvent =
  | { type: "APPLY_DOCUMENT"; worldJsonText: string }
  | { type: "SELECT_CELLS"; selection: CellReference[] }
  | { type: "PAINT_CELLS"; stateName: string; value: number }
  | { type: "BIND_APPEARANCE"; stateName: string; value: number; appearance: ValueAppearance }
  | { type: "DOCUMENT_UNDO" | "DOCUMENT_REDO" }
  | {
  type: "LOAD_WORLD";
  worldJsonText: string;
} | {
  type: "SELECT_TOPOLOGY";
  name: string;
} | {
  type: "PREVIEW_CELL";
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
const transient = { hoveredMask: null, probedRayBeam: null };
function load(text: string, candidate?: any): WorldSimulationContext {
  const inspected = inspectWorldDocument(text);
  const world = candidate ?? inspected.world, previewIssues = inspected.previewIssues;
  const topologies: TopologyDefinition[] = world.state.lattices ?? [];
  const stateDefinitions = world.state.world ?? [];
  return {
    worldJsonText: text, parsedWorld: world, previewIssues, error: null, topologies,
    topologyMap: Object.fromEntries(topologies.map(t => [t.name, t])),
    selectedTopologyName: topologies[0]?.name ?? "", stateDefinitions, rules: world.rules ?? [],
    history: [createInitialSnapshot(stateDefinitions)], historyIndex: 0, ...transient, isDirty: false,
    documentHistory: [{ text, label: "Opened document" }], documentIndex: 0, savedDocumentText: text, selection: []
  };
}
function preserveGeometry(context: WorldSimulationContext, loaded: WorldSimulationContext): WorldSimulationContext {
  const stable = loaded.topologies.map(topology => {
    const previous = context.topologyMap[topology.name];
    return previous && JSON.stringify(previous) === JSON.stringify(topology) ? previous : topology;
  });
  const topologies = stable.length === context.topologies.length && stable.every((t, i) => t === context.topologies[i]) ? context.topologies : stable;
  return { ...loaded, topologies, topologyMap: Object.fromEntries(topologies.map(t => [t.name, t])) };
}
function replaceDocument(context: WorldSimulationContext, world: any, label = "Edit document", sourceText?: string) {
  try {
    if (world === context.parsedWorld) return { error: null };
    const text = sourceText ?? JSON.stringify(world, null, 2);
    if (text === context.worldJsonText) return { error: null };
    const loaded = preserveGeometry(context, load(text, world));
    const documentHistory = [...context.documentHistory.slice(0, context.documentIndex + 1), { text, label }];
    let characters = documentHistory.reduce((sum, revision) => sum + revision.text.length, 0);
    while (documentHistory.length > 1 && (documentHistory.length > 64 || characters > 8 * 1024 * 1024)) characters -= documentHistory.shift()!.text.length;
    return { ...loaded, documentHistory, documentIndex: documentHistory.length - 1,
      savedDocumentText: context.savedDocumentText, isDirty: text !== context.savedDocumentText,
      selectedTopologyName: loaded.topologyMap[context.selectedTopologyName] ? context.selectedTopologyName : loaded.selectedTopologyName,
      selection: validSelection(world, context.selection) };
  } catch (error) { return { error: (error as Error).message }; }
}
function travelDocument(context: WorldSimulationContext, delta: number) {
  const documentIndex = Math.max(0, Math.min(context.documentHistory.length - 1, context.documentIndex + delta));
  if (documentIndex === context.documentIndex) return {};
  const text = context.documentHistory[documentIndex].text;
  const loaded = preserveGeometry(context, load(text));
  return { ...loaded, documentHistory: context.documentHistory, documentIndex,
    savedDocumentText: context.savedDocumentText, isDirty: text !== context.savedDocumentText,
    selectedTopologyName: loaded.topologyMap[context.selectedTopologyName] ? context.selectedTopologyName : loaded.selectedTopologyName,
    selection: validSelection(loaded.parsedWorld, context.selection) };
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
        case "SELECT_TOPOLOGY": return c.topologyMap[e.name] ? { selectedTopologyName: e.name, ...transient } : {};
        case "SET_DIRTY": return { isDirty: e.isDirty, savedDocumentText: e.isDirty ? c.savedDocumentText : c.worldJsonText };
        case "DOCUMENT_UNDO": return travelDocument(c, -1);
        case "DOCUMENT_REDO": return travelDocument(c, 1);
        case "SELECT_CELLS": return { selection: validSelection(c.parsedWorld, e.selection), error: null };
        case "APPLY_DOCUMENT": try { return replaceDocument(c, inspectWorldDocument(e.worldJsonText).world, "Apply JSON", e.worldJsonText); }
          catch (error) { return { error: (error as Error).message }; }
        case "PAINT_CELLS": try { return replaceDocument(c, paintCells(c.parsedWorld, e.stateName, c.selection, e.value), "Paint " + c.selection.length + " cells in " + e.stateName); }
          catch (error) { return { error: (error as Error).message }; }
        case "BIND_APPEARANCE": try { return replaceDocument(c, bindAppearance(c.parsedWorld, e.stateName, e.value, e.appearance), "Bind appearance for " + e.stateName); }
          catch (error) { return { error: (error as Error).message }; }
        case "HOVER_CELL": return {}; // Cell inspection is local UI state and never evaluates rules.
        case "HOVER_MASK": return { hoveredMask: e.mask };
        case "PROBE_RAY": return { probedRayBeam: e.ray };
        case "UNDO": return { historyIndex: Math.max(0, c.historyIndex - 1), error: null, ...transient };
        case "REDO": return { historyIndex: Math.min(c.history.length - 1, c.historyIndex + 1), error: null, ...transient };
        case "JUMP_TO_TICK": return { historyIndex: Math.max(0, Math.min(c.history.length - 1, e.tickIndex)), error: null, ...transient };
        case "RESET_WORLD": return { history: [createInitialSnapshot(c.stateDefinitions)], historyIndex: 0, error: null, ...transient };
        case "STATE_CHANGE": return step(c, { [e.stateName]: e.newValue }, "Edit preview register " + e.stateName);
        case "DISPATCH_ACTION": return step(c, e.mutations, e.intent);
        case "PREVIEW_CELL": try {
          const mutations = previewCellAction(c.history[c.historyIndex], c.topologyMap[c.selectedTopologyName], c.stateDefinitions, e.cellIdx);
          return step(c, mutations, "Preview input at cell " + e.cellIdx, true);
        } catch (error) { return { error: (error as Error).message }; }
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
  on: Object.fromEntries(["LOAD_WORLD", "SELECT_TOPOLOGY", "SET_DIRTY", "HOVER_CELL", "HOVER_MASK", "PROBE_RAY", "UNDO", "REDO", "JUMP_TO_TICK", "RESET_WORLD", "STATE_CHANGE", "DISPATCH_ACTION", "PREVIEW_CELL", "APPLY_DOCUMENT", "SELECT_CELLS", "PAINT_CELLS", "BIND_APPEARANCE", "DOCUMENT_UNDO", "DOCUMENT_REDO", "ADD_RULE", "SAVE_STATE_DEFINITIONS", "SAVE_TOPOLOGY"].map(type => [type, { actions: "reduce" as const }]))
});
// Fine-grained Reactive Selectors for React 19 / @xstate/react
export const selectCurrentSnapshot = (s: {
  context: WorldSimulationContext;
}): TickSnapshot | null => s.context.history[s.context.historyIndex] ?? null;
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
export const selectHoveredMask = (s: {
  context: WorldSimulationContext;
}): bigint | null => s.context.hoveredMask;
export const selectProbedRayBeam = (s: {
  context: WorldSimulationContext;
}): RayBeam | null => s.context.probedRayBeam;
export const selectDisplayedRayBeam = (s: {
  context: WorldSimulationContext;
}): RayBeam | null => s.context.probedRayBeam;
export const selectCanUndo = (s: {
  context: WorldSimulationContext;
}): boolean => s.context.historyIndex > 0;
export const selectCanRedo = (s: {
  context: WorldSimulationContext;
}): boolean => s.context.historyIndex < s.context.history.length - 1;
export const selectLastDeltas = (s: {
  context: WorldSimulationContext;
}): StateDelta[] => s.context.history[s.context.historyIndex]?.trace?.allDeltas ?? [];
