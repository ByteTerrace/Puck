import { WorldRule, TopologyDefinition, evaluatePuckExpression, evaluatePuckPredicate, resolveEffectKey } from "./evaluator";
export interface StateDelta {
  target: string;
  key?: string | number;
  oldValue: any;
  newValue: any;
}
export interface RuleExecutionEvent {
  ruleName: string;
  mode: "Edge" | "Level";
  fired: boolean;
  gateSummary: string;
  deltas: StateDelta[];
  gate?: any;
  error?: string;
  gateState?: Record<string, any>;
  gateBoardCells?: Record<string, Record<number, number>>;
}
export interface StepTrace {
  tick: number;
  intentDescription: string;
  ruleEvents: RuleExecutionEvent[];
  allDeltas: StateDelta[];
}
export interface StepExecutionResult {
  nextState: Record<string, any>;
  nextBoardCells: Record<string, Record<number, number>>;
  nextEdgeLatches: Record<string, boolean>;
  trace: StepTrace;
}
/** One ordered tick of the supported offline subset. Snapshots share unchanged rows. */
export function executeSimulationTick(tickNumber: number, intentDescription: string, mutations: Record<string, any>, currentState: Record<string, any>, currentBoardCells: Record<string, Record<number, number>>, rules: WorldRule[], topologies: Record<string, TopologyDefinition>, edgeLatches: Record<string, boolean> = {}): StepExecutionResult {
  if(rules.length > 256)
    throw new Error("Preview supports up to 256 rules.");
  const deadline = performance.now() + 25;
  const budget = () => {
    if(performance.now() > deadline)
      throw new Error("Preview tick exceeded 25 ms. Simplify the document or use native execution.");
  };
  let state = { ...currentState, ...mutations };
  let boardCells = currentBoardCells;
  const nextEdgeLatches = { ...edgeLatches };
  const allDeltas: StateDelta[] = Object.entries(mutations).filter(([key, value]) => currentState[key] !== value)
    .map(([target, newValue]) => ({ target, oldValue: currentState[target], newValue }));
  const ruleEvents: RuleExecutionEvent[] = [];
  for(const rule of rules) {
    budget();
    if(rule.forEach)
      throw new Error("Bound rules require native preview.");
    const gateState = state, gateBoardCells = boardCells;
    const gate = evaluatePuckPredicate(rule.gate, state, boardCells);
    const mode = rule.mode ?? "Level";
    const fired = gate.passed && (mode !== "Edge" || !edgeLatches[rule.name]);
    if(mode === "Edge")
      nextEdgeLatches[rule.name] = gate.passed;
    const deltas: StateDelta[] = [];
    if((rule.effects?.length ?? 0) > 32)
      throw new Error("Too many preview effects.");
    if(fired)
      for(const effect of rule.effects ?? []) {
        budget();
        if(!["setState", "addState"].includes(effect.$type))
          throw new Error("Unsupported preview effect: " + effect.$type);
        const target = effect.state;
        const keyed = effect.key !== undefined;
        const resolvedKey = keyed ? resolveEffectKey(effect.key, state) : null;
        if(keyed && typeof resolvedKey !== "number")
          throw new Error("Unresolved preview cell key.");
        const key = keyed ? Number(resolvedKey) : undefined;
        if(keyed && (!Number.isInteger(key) || key! < 0 || key! >= 4096 || !Object.hasOwn(boardCells, target)))
          throw new Error("Invalid preview cell target.");
        if(!keyed && !Object.hasOwn(state, target))
          throw new Error("Unknown preview register: " + target);
        const oldValue = keyed ? boardCells[target][key!] ?? 0 : state[target];
        let newValue = effect.fromState !== undefined ? state[effect.fromState] : effect.expression !== undefined
          ? evaluatePuckExpression(effect.expression, state, boardCells, topologies) : effect.value ?? 0;
        if(effect.$type === "addState")
          newValue = evaluatePuckExpression("previous + amount", { previous: oldValue, amount: newValue }, {}, {});
        if(!["number", "bigint", "boolean"].includes(typeof newValue) || (typeof newValue === "number" && !Number.isSafeInteger(newValue)))
          throw new Error("Effect is not an exact preview integer.");
        if(keyed && !Number.isSafeInteger(Number(newValue)))
          throw new Error("Board cells require exact JSON integers in offline preview.");
        if(keyed)
          newValue = Number(newValue);
        if(oldValue === newValue)
          continue;
        const delta = { target, key, oldValue, newValue };
        deltas.push(delta);
        allDeltas.push(delta);
        if(keyed)
          boardCells = { ...boardCells, [target]: { ...boardCells[target], [key!]: newValue } };
        else
          state = { ...state, [target]: newValue };
      }
    ruleEvents.push({
      ruleName: rule.name, mode, fired, gateSummary: gate.details,
      deltas, gate: rule.gate, gateState, gateBoardCells
    });
  }
  return {
    nextState: state, nextBoardCells: boardCells, nextEdgeLatches,
    trace: { tick: tickNumber, intentDescription, ruleEvents, allDeltas }
  };
}
/** A demo input tick followed by an idle tick to observe request acknowledgements. */
export function executePreviewAction(...args: Parameters<typeof executeSimulationTick>): StepExecutionResult {
  const input = executeSimulationTick(...args);
  const idle = executeSimulationTick(args[0] + 1, "Idle after input", {}, input.nextState, input.nextBoardCells, args[5], args[6], input.nextEdgeLatches);
  return {
    ...idle, trace: {
      ...idle.trace, intentDescription: args[1],
      ruleEvents: [...input.trace.ruleEvents, ...idle.trace.ruleEvents],
      allDeltas: [...input.trace.allDeltas, ...idle.trace.allDeltas]
    }
  };
}
