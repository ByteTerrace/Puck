import {
  WorldRule,
  TopologyDefinition,
  computeBoardMask,
  boardShift,
  getTopologyCoordinates,
} from "./evaluator";
import { executePreviewAction, StepTrace } from "./tickRunner";

export interface WorldTestScenario {
  id: string;
  name: string;
  description: string;
  initialState?: Record<string, any>;
  initialBoardCells?: Record<string, Record<number, number>>;
  moves: Array<{
    cell?: number;
    mutations?: Record<string, any>;
    description?: string;
  }>;
  assertions: {
    state?: Record<string, any>;
    boardCells?: Record<string, Record<number, number>>;
    rulesFiredAtLeastOnce?: string[];
  };
}

export interface ScenarioExecutionResult {
  scenarioId: string;
  scenarioName: string;
  passed: boolean;
  durationMs: number;
  ticksExecuted: number;
  finalState: Record<string, any>;
  errors: string[];
  traces: StepTrace[];
}

export interface MonteCarloSummary {
  simulationsCount: number;
  player1Wins: number;
  player2Wins: number;
  draws: number;
  averageTicks: number;
  deadlocksDetected: number;
  durationMs: number;
  ruleCoverage: Record<
    string,
    {
      ruleName: string;
      mode: "Edge" | "Level";
      timesFired: number;
      gamesFiredIn: number;
      coveragePercentage: number;
    }
  >;
}

// Executes a single formal test scenario against the given world
export function runWorldScenario(
  scenario: WorldTestScenario,
  rules: WorldRule[],
  topologies: Record<string, TopologyDefinition>,
  authoredState?: {state:Record<string,any>;boardCells:Record<string,Record<number,number>>}
): ScenarioExecutionResult {
  const startTime = performance.now();
  const defaultInitialState: Record<string, any> = {
    tttActive: 1,
    tttWinner: 0,
    tttMoveApplied: 0,
    tttMoveCount: 0,
    tttBoardVersion: 0,
    tttWinCheckVersion: 0,
    tttMaskX: 0n,
    tttMaskO: 0n,
    tttXWin: 0,
    tttOWin: 0,
    hexTurn: 1,
    hexWinner: 0,
    chessTurn: 1,
  };

  let currentState: Record<string, any> = {
    ...(authoredState?.state ?? defaultInitialState),
    ...(scenario.initialState ?? {}),
  };
  let currentBoardCells: Record<string, Record<number, number>> = {
    ...(authoredState?.boardCells ?? { tttBoard: {} }),
  };

  if (scenario.initialBoardCells) {
    for (const [k, v] of Object.entries(scenario.initialBoardCells)) {
      currentBoardCells[k] = { ...v };
    }
  }

  if (scenario.moves.length > 128) throw new Error("Scenarios are limited to 128 moves.");
  const traces: StepTrace[] = [];
  const errors: string[] = [];
  const rulesFiredSet = new Set<string>();

  let latches: Record<string,boolean> = {};
  let tick = 0;
  for (const move of scenario.moves) {
    tick++;
    const moveReqSeq = Number(currentState["tttMoveRequest"] ?? currentState["hexMoveRequest"] ?? 0) + 1;
    const mutations: Record<string, any> = {
      ...(move.mutations ?? {}),
    };

    if (move.cell !== undefined) {
      mutations["tttMoveCell"] = move.cell;
      mutations["tttMoveRequest"] = moveReqSeq;
      mutations["hexMoveCell"] = move.cell;
      mutations["hexMoveRequest"] = moveReqSeq;
    }

    const res = executePreviewAction(
      tick,
      move.description ?? `Scenario Move ${tick}`,
      mutations,
      currentState,
      currentBoardCells,
      rules,
      topologies, latches
    );
    tick = res.trace.tick; latches = res.nextEdgeLatches;

    currentState = res.nextState;
    currentBoardCells = res.nextBoardCells;
    traces.push(res.trace);

    for (const evt of res.trace.ruleEvents) {
      if (evt.fired) {
        rulesFiredSet.add(evt.ruleName);
      }
    }
  }

  // Check state assertions
  if (scenario.assertions.state) {
    for (const [key, expectedVal] of Object.entries(scenario.assertions.state)) {
      const actualVal = currentState[key];
      if (actualVal !== expectedVal) {
        errors.push(
          `State mismatch for '${key}': expected ${expectedVal}, got ${actualVal}`
        );
      }
    }
  }

  // Check cell assertions
  if (scenario.assertions.boardCells) {
    for (const [boardName, cells] of Object.entries(scenario.assertions.boardCells)) {
      for (const [cIdx, expectedVal] of Object.entries(cells)) {
        const actualVal = currentBoardCells[boardName]?.[Number(cIdx)] ?? 0;
        if (actualVal !== expectedVal) {
          errors.push(
            `Board '${boardName}' cell #${cIdx} mismatch: expected ${expectedVal}, got ${actualVal}`
          );
        }
      }
    }
  }

  // Check required rule executions
  if (scenario.assertions.rulesFiredAtLeastOnce) {
    for (const reqRule of scenario.assertions.rulesFiredAtLeastOnce) {
      if (!rulesFiredSet.has(reqRule)) {
        errors.push(`Required rule '${reqRule}' never fired during scenario execution.`);
      }
    }
  }

  const durationMs = Math.round(performance.now() - startTime);

  return {
    scenarioId: scenario.id,
    scenarioName: scenario.name,
    passed: errors.length === 0,
    durationMs,
    ticksExecuted: tick,
    finalState: currentState,
    errors,
    traces,
  };
}

// Monte Carlo Rollout Simulator
export function runMonteCarloRollout(
  simulationsCount: number,
  rules: WorldRule[],
  topologies: Record<string, TopologyDefinition>,
  initialState: Record<string, any>,
  initialBoardCells: Record<string, Record<number, number>>,
  options?: {
    maxTicks?: number;
    policy?: "random" | "greedy";
  }
): MonteCarloSummary {
  if (!Number.isInteger(simulationsCount) || simulationsCount < 1 || simulationsCount > 200) throw new Error("Choose 1 to 200 games.");
  if (!Object.hasOwn(initialState,"tttMoveRequest") && !Object.hasOwn(initialState,"hexMoveRequest")) throw new Error("This document has no supported board input adapter.");
  const startTime = performance.now();
  const maxTicks = options?.maxTicks ?? 128;
  if (!Number.isInteger(maxTicks) || maxTicks < 1 || maxTicks > 128) throw new Error("Rollouts are limited to 128 ticks per game.");
  const policy = options?.policy ?? "random";

  let p1Wins = 0;
  let p2Wins = 0;
  let draws = 0;
  let deadlocks = 0;
  let totalTicks = 0;

  const ruleStats: Record<string, { ruleName: string; mode: "Edge" | "Level"; timesFired: number; gamesFiredIn: number }> = {};
  for (const r of rules) {
    ruleStats[r.name] = {
      ruleName: r.name,
      mode: r.mode === "Edge" ? "Edge" : "Level",
      timesFired: 0,
      gamesFiredIn: 0,
    };
  }

  const primaryTopo = Object.values(topologies)[0];
  const allCoords = primaryTopo ? getTopologyCoordinates(primaryTopo) : [];
  const boardSize = allCoords.length > 0 ? allCoords.length : 64;
  if (boardSize > 64) throw new Error("Demo rollouts support boards up to 64 cells.");

  for (let sim = 0; sim < simulationsCount; sim++) {
    let state = { ...initialState };
    let boardCells: Record<string, Record<number, number>> = {};
    for (const [k, v] of Object.entries(initialBoardCells)) {
      boardCells[k] = { ...v };
    }

    const rulesFiredThisGame = new Set<string>();
    let latches: Record<string,boolean> = {};
    let tick = 0;
    let gameFinished = false;

    while (tick < maxTicks && !gameFinished) {
      tick++;
      const currentWinner = state["tttWinner"] ?? state["hexWinner"] ?? 0;
      if (currentWinner !== 0) {
        gameFinished = true;
        break;
      }

      // Determine available empty cells
      const activeBoardName = Object.keys(boardCells)[0] ?? "tttBoard";
      const currentBoard = boardCells[activeBoardName] ?? {};
      const emptyCells: number[] = [];
      for (let i = 0; i < boardSize; i++) {
        if (!currentBoard[i] || currentBoard[i] === 0) {
          emptyCells.push(i);
        }
      }

      if (emptyCells.length === 0) {
        gameFinished = true;
        break;
      }

      let chosenCell = emptyCells[Math.floor(Math.random() * emptyCells.length)];

      // Greedy policy: if a cell completes a win immediately, pick it!
      if (policy === "greedy" && primaryTopo && primaryTopo.directions) {
        const activePlayer = state["tttActive"] ?? 1;
        const currentMask = computeBoardMask(currentBoard, activePlayer);

        for (const candidate of emptyCells) {
          const testMask = currentMask | (1n << BigInt(candidate));
          let winsCandidate = false;
          for (const dir of primaryTopo.directions) {
            const s1 = boardShift(testMask, primaryTopo, dir.name);
            const s2 = boardShift(s1 & testMask, primaryTopo, dir.name);
            const s3 = boardShift(s2 & testMask, primaryTopo, dir.name);
            if ((s3 & testMask) !== 0n) {
              winsCandidate = true;
              break;
            }
          }
          if (winsCandidate) {
            chosenCell = candidate;
            break;
          }
        }
      }

      const moveReqSeq = Number(state["tttMoveRequest"] ?? 0) + 1;
      const res = executePreviewAction(
        tick,
        `Rollout #${sim + 1} Tick #${tick}`,
        {
          tttMoveCell: chosenCell,
          tttMoveRequest: moveReqSeq,
          hexMoveCell: chosenCell,
          hexMoveRequest: moveReqSeq,
        },
        state,
        boardCells,
        rules,
        topologies, latches
      );
      tick = res.trace.tick; latches = res.nextEdgeLatches;

      state = res.nextState;
      boardCells = res.nextBoardCells;

      for (const evt of res.trace.ruleEvents) {
        if (evt.fired) {
          rulesFiredThisGame.add(evt.ruleName);
          if (ruleStats[evt.ruleName]) {
            ruleStats[evt.ruleName].timesFired++;
          }
        }
      }

      const postWinner = state["tttWinner"] ?? state["hexWinner"] ?? 0;
      if (postWinner !== 0) {
        gameFinished = true;
      }
    }

    totalTicks += tick;
    for (const rName of rulesFiredThisGame) {
      if (ruleStats[rName]) {
        ruleStats[rName].gamesFiredIn++;
      }
    }

    const finalWinner = state["tttWinner"] ?? state["hexWinner"] ?? 0;
    if (finalWinner === 1) p1Wins++;
    else if (finalWinner === 2) p2Wins++;
    else if (finalWinner === 3) draws++;
    else {
      // Reached maxTicks without terminal state
      deadlocks++;
    }
  }

  const durationMs = Math.round(performance.now() - startTime);

  const ruleCoverage: MonteCarloSummary["ruleCoverage"] = {};
  for (const [rName, stat] of Object.entries(ruleStats)) {
    ruleCoverage[rName] = {
      ...stat,
      coveragePercentage:
        simulationsCount > 0
          ? Math.round((stat.gamesFiredIn / simulationsCount) * 100)
          : 0,
    };
  }

  return {
    simulationsCount,
    player1Wins: p1Wins,
    player2Wins: p2Wins,
    draws,
    averageTicks: simulationsCount > 0 ? Math.round((totalTicks / simulationsCount) * 10) / 10 : 0,
    deadlocksDetected: deadlocks,
    durationMs,
    ruleCoverage,
  };
}

// Built-in standard test suites for catalog worlds
export const STANDARD_WORLD_SCENARIOS: Record<string, WorldTestScenario[]> = {
  tictactoe: [
    {
      id: "ttt-horizontal-l8",
      name: "Horizontal Row Win (L8)",
      description: "Verifies that Player 1 marks at cells 0, 1, 2, 3 trigger tttWinner = 1 along horizontal ray L8.",
      moves: [
        { cell: 0 }, { cell: 4 },
        { cell: 1 }, { cell: 5 },
        { cell: 2 }, { cell: 6 },
        { cell: 3 },
      ],
      assertions: {
        state: { tttWinner: 1, tttXWin: 1, tttOWin: 0 },
        rulesFiredAtLeastOnce: ["ttt-place-mark", "ttt-check-win"],
      },
    },
    {
      id: "ttt-vertical-l0",
      name: "Vertical Pillar Win (L0)",
      description: "Verifies that Player 1 marks at cells 0, 16, 32, 48 trigger tttWinner = 1 along vertical ray L0.",
      moves: [
        { cell: 0 }, { cell: 1 },
        { cell: 16 }, { cell: 2 },
        { cell: 32 }, { cell: 3 },
        { cell: 48 },
      ],
      assertions: {
        state: { tttWinner: 1, tttXWin: 1, tttOWin: 0 },
        rulesFiredAtLeastOnce: ["ttt-place-mark", "ttt-check-win"],
      },
    },
    {
      id: "ttt-space-diagonal-l4",
      name: "Full 3D Space-Diagonal Win (L4)",
      description: "Verifies 3D diagonal through cube interior from corner 0 to opposite corner 63 (0, 21, 42, 63).",
      moves: [
        { cell: 0 }, { cell: 1 },
        { cell: 21 }, { cell: 2 },
        { cell: 42 }, { cell: 3 },
        { cell: 63 },
      ],
      assertions: {
        state: { tttWinner: 1, tttXWin: 1 },
        rulesFiredAtLeastOnce: ["ttt-check-win"],
      },
    },
    {
      id: "ttt-reject-occupied",
      name: "Reject Illegal Overwrite",
      description: "Ensures playing on an already-occupied cell fires ttt-reject-illegal and does not overwrite.",
      moves: [
        { cell: 12 }, // P1 plays 12
        { cell: 12 }, // P2 attempts to overwrite 12
      ],
      assertions: {
        state: { tttActive: 2 },
        boardCells: { tttBoard: { 12: 1 } },
        rulesFiredAtLeastOnce: ["ttt-reject-illegal"],
      },
    },
  ],
};
