import {
  WorldRule,
  TopologyDefinition,
  evaluatePuckExpression,
  evaluatePuckPredicate,
  resolveEffectKey,
} from "./evaluator";

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
  trace: StepTrace;
}

export function executeSimulationTick(
  tickNumber: number,
  intentDescription: string,
  stateMutationsBeforeTick: Record<string, any>,
  currentState: Record<string, any>,
  currentBoardCells: Record<string, Record<number, number>>,
  rules: WorldRule[],
  topologies: Record<string, TopologyDefinition>
): StepExecutionResult {
  const state: Record<string, any> = { ...currentState, ...stateMutationsBeforeTick };
  const boardCells: Record<string, Record<number, number>> = {};

  // Deep copy current board cells
  for (const bName of Object.keys(currentBoardCells)) {
    boardCells[bName] = { ...currentBoardCells[bName] };
  }

  const allDeltas: StateDelta[] = [];

  // Record initial input mutations as deltas
  for (const [key, newVal] of Object.entries(stateMutationsBeforeTick)) {
    const oldVal = currentState[key];
    if (oldVal !== newVal) {
      allDeltas.push({ target: key, oldValue: oldVal, newValue: newVal });
    }
  }

  const ruleEvents: RuleExecutionEvent[] = [];

  // Helper to execute an effect list
  const applyEffects = (
    _ruleName: string,
    _mode: "Edge" | "Level",
    effects: any[]
  ): StateDelta[] => {
    const deltas: StateDelta[] = [];

    for (const eff of effects) {
      const stateTarget = eff.state;
      let calculatedValue: any = 0;

      if (eff.fromState !== undefined) {
        calculatedValue = state[eff.fromState] ?? 0;
      } else if (eff.expression !== undefined) {
        calculatedValue = evaluatePuckExpression(
          eff.expression,
          state,
          boardCells,
          topologies
        );
      } else if (eff.value !== undefined) {
        calculatedValue = eff.value;
      }

      if (eff.$type === "addState") {
        const prev = Number(state[stateTarget] ?? 0);
        calculatedValue = prev + Number(calculatedValue);
      }

      // Check if writing to a board cell index via key
      if (eff.key) {
        const resolvedKey = resolveEffectKey(eff.key, state);
        if (resolvedKey !== null && typeof resolvedKey === "number") {
          if (!boardCells[stateTarget]) boardCells[stateTarget] = {};
          const oldVal = boardCells[stateTarget][resolvedKey] ?? 0;
          boardCells[stateTarget][resolvedKey] = Number(calculatedValue);

          const delta: StateDelta = {
            target: stateTarget,
            key: resolvedKey,
            oldValue: oldVal,
            newValue: Number(calculatedValue),
          };
          deltas.push(delta);
          allDeltas.push(delta);
        }
      } else {
        // Scalar register write
        const oldVal = state[stateTarget];
        state[stateTarget] = calculatedValue;

        const delta: StateDelta = {
          target: stateTarget,
          oldValue: oldVal,
          newValue: calculatedValue,
        };
        deltas.push(delta);
        allDeltas.push(delta);
      }
    }

    return deltas;
  };

  // Phase 1: Edge Rules Execution
  const edgeRules = rules.filter((r) => r.mode === "Edge");
  for (const rule of edgeRules) {
    const gateEval = evaluatePuckPredicate(rule.gate, state, boardCells);
    if (gateEval.passed) {
      const deltas = applyEffects(rule.name, "Edge", rule.effects ?? []);
      ruleEvents.push({
        ruleName: rule.name,
        mode: "Edge",
        fired: true,
        gateSummary: gateEval.details,
        deltas,
        gate: rule.gate,
      });
    } else {
      ruleEvents.push({
        ruleName: rule.name,
        mode: "Edge",
        fired: false,
        gateSummary: gateEval.details,
        deltas: [],
        gate: rule.gate,
      });
    }
  }

  // Phase 2: Reactive Level Rules Cascades
  const levelRules = rules.filter((r) => r.mode !== "Edge");
  let cascadeIteration = 0;
  let hasMoreCascades = true;

  while (hasMoreCascades && cascadeIteration++ < 20) {
    hasMoreCascades = false;

    for (const rule of levelRules) {
      const gateEval = evaluatePuckPredicate(rule.gate, state, boardCells);
      if (gateEval.passed) {
        const deltas = applyEffects(rule.name, "Level", rule.effects ?? []);
        ruleEvents.push({
          ruleName: rule.name,
          mode: "Level",
          fired: true,
          gateSummary: gateEval.details,
          deltas,
          gate: rule.gate,
        });

        if (deltas.length > 0) {
          hasMoreCascades = true;
        }
      }
    }
  }

  return {
    nextState: state,
    nextBoardCells: boardCells,
    trace: {
      tick: tickNumber,
      intentDescription,
      ruleEvents,
      allDeltas,
    },
  };
}
