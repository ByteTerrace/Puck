// World catalog containing authentic game definitions from src/Puck.World/Assets/worlds/games/

export interface WorldCatalogItem {
  id: string;
  name: string;
  subtitle: string;
  category: "Strategy" | "Lattice CAD" | "Board" | "Ring";
  world: any;
}

export const TIC_TAC_TOE_WORLD = {
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
          { $type: "compareState", state: "tttBoard", key: "$cell:tttMoveCell:$value", value: 0, comparison: "Equal" },
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
      name: "ttt-reject-out-of-range",
      mode: "Edge",
      gate: {
        $type: "all",
        predicates: [
          { $type: "compareState", state: "tttMoveRequest", comparandState: "tttMoveApplied", comparison: "NotEqual" },
          {
            $type: "any",
            predicates: [
              { $type: "compareState", state: "tttMoveCell", value: 0, comparison: "Less" },
              { $type: "compareState", state: "tttMoveCell", value: 63, comparison: "Greater" },
            ],
          },
        ],
      },
      effects: [
        { $type: "setState", state: "tttMoveApplied", fromState: "tttMoveRequest" },
      ],
    },
    {
      name: "ttt-reject-illegal",
      mode: "Edge",
      gate: {
        $type: "all",
        predicates: [
          { $type: "compareState", state: "tttMoveRequest", comparandState: "tttMoveApplied", comparison: "NotEqual" },
          { $type: "compareState", state: "tttMoveCell", value: 0, comparison: "GreaterOrEqual" },
          { $type: "compareState", state: "tttMoveCell", value: 63, comparison: "LessOrEqual" },
          {
            $type: "any",
            predicates: [
              { $type: "compareState", state: "tttWinner", value: 0, comparison: "NotEqual" },
              { $type: "compareState", state: "tttBoard", key: "$cell:tttMoveCell:$value", value: 0, comparison: "NotEqual" },
            ],
          },
        ],
      },
      effects: [
        { $type: "setState", state: "tttMoveApplied", fromState: "tttMoveRequest" },
      ],
    },
    {
      name: "ttt-check-win",
      mode: "Level",
      gate: {
        $type: "compareState",
        state: "tttBoardVersion",
        comparandState: "tttWinCheckVersion",
        comparison: "NotEqual",
      },
      effects: [
        { $type: "setState", state: "tttMaskX", expression: "$board:mask:tttBoard:1:1" },
        {
          $type: "setState",
          state: "tttXWin",
          expression: "((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L0), tttCube, L0), tttCube, L0)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L1), tttCube, L1), tttCube, L1)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L2), tttCube, L2), tttCube, L2)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L3), tttCube, L3), tttCube, L3)) != 0)",
        },
        {
          $type: "setState",
          state: "tttXWin",
          expression: "tttXWin | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L4), tttCube, L4), tttCube, L4)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L5), tttCube, L5), tttCube, L5)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L6), tttCube, L6), tttCube, L6)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L7), tttCube, L7), tttCube, L7)) != 0)",
        },
        {
          $type: "setState",
          state: "tttXWin",
          expression: "tttXWin | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L8), tttCube, L8), tttCube, L8)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L9), tttCube, L9), tttCube, L9)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L10), tttCube, L10), tttCube, L10)) != 0) | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L11), tttCube, L11), tttCube, L11)) != 0)",
        },
        {
          $type: "setState",
          state: "tttXWin",
          expression: "tttXWin | ((tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX & boardShift(tttMaskX, tttCube, L12), tttCube, L12), tttCube, L12)) != 0)",
        },
        { $type: "setState", state: "tttMaskO", expression: "$board:mask:tttBoard:2:2" },
        {
          $type: "setState",
          state: "tttOWin",
          expression: "((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L0), tttCube, L0), tttCube, L0)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L1), tttCube, L1), tttCube, L1)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L2), tttCube, L2), tttCube, L2)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L3), tttCube, L3), tttCube, L3)) != 0)",
        },
        {
          $type: "setState",
          state: "tttOWin",
          expression: "tttOWin | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L4), tttCube, L4), tttCube, L4)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L5), tttCube, L5), tttCube, L5)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L6), tttCube, L6), tttCube, L6)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L7), tttCube, L7), tttCube, L7)) != 0)",
        },
        {
          $type: "setState",
          state: "tttOWin",
          expression: "tttOWin | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L8), tttCube, L8), tttCube, L8)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L9), tttCube, L9), tttCube, L9)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L10), tttCube, L10), tttCube, L10)) != 0) | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L11), tttCube, L11), tttCube, L11)) != 0)",
        },
        {
          $type: "setState",
          state: "tttOWin",
          expression: "tttOWin | ((tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO & boardShift(tttMaskO, tttCube, L12), tttCube, L12), tttCube, L12)) != 0)",
        },
        {
          $type: "setState",
          state: "tttWinner",
          expression: "tttXWin ? 1 : tttOWin ? 2 : tttMoveCount >= 64 ? 3 : 0",
        },
        {
          $type: "setState",
          state: "tttWinCheckVersion",
          fromState: "tttBoardVersion",
        },
      ],
    },
  ],
  state: {
    lattices: [
      {
        $type: "box",
        name: "tttCube",
        width: 4,
        depth: 4,
        layers: 4,
        cellSize: 1,
        layerHeight: 1,
        origin: [0, -32, 0],
        directions: [
          { name: "L0", x: 0, y: 0, z: 1 },
          { name: "L1", x: 0, y: 1, z: -1 },
          { name: "L2", x: 0, y: 1, z: 0 },
          { name: "L3", x: 0, y: 1, z: 1 },
          { name: "L4", x: 1, y: -1, z: -1 },
          { name: "L5", x: 1, y: -1, z: 0 },
          { name: "L6", x: 1, y: -1, z: 1 },
          { name: "L7", x: 1, y: 0, z: -1 },
          { name: "L8", x: 1, y: 0, z: 0 },
          { name: "L9", x: 1, y: 0, z: 1 },
          { name: "L10", x: 1, y: 1, z: -1 },
          { name: "L11", x: 1, y: 1, z: 0 },
          { name: "L12", x: 1, y: 1, z: 1 },
          { name: "L0Back", x: 0, y: 0, z: -1 },
          { name: "L1Back", x: 0, y: -1, z: 1 },
          { name: "L2Back", x: 0, y: -1, z: 0 },
          { name: "L3Back", x: 0, y: -1, z: -1 },
          { name: "L4Back", x: -1, y: 1, z: 1 },
          { name: "L5Back", x: -1, y: 1, z: 0 },
          { name: "L6Back", x: -1, y: 1, z: -1 },
          { name: "L7Back", x: -1, y: 0, z: 1 },
          { name: "L8Back", x: -1, y: 0, z: 0 },
          { name: "L9Back", x: -1, y: 0, z: -1 },
          { name: "L10Back", x: -1, y: -1, z: 1 },
          { name: "L11Back", x: -1, y: -1, z: 0 },
          { name: "L12Back", x: -1, y: -1, z: -1 },
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
      { name: "tttWinCheckVersion", kind: "int", nonNegative: true, value: 0 },
      { name: "tttWinner", kind: "int", min: 0, max: 3, value: 0 },
      { name: "tttMoveCount", kind: "int", min: 0, max: 64, value: 0 },
      { name: "tttXWin", kind: "int", min: 0, max: 1, value: 0 },
      { name: "tttOWin", kind: "int", min: 0, max: 1, value: 0 },
      { name: "tttMaskX", kind: "int", value: 0 },
      { name: "tttMaskO", kind: "int", value: 0 },
    ],
  },
};

export const MANCALA_WORLD = {
  rules: [
    {
      name: "mancala-select-pit",
      mode: "Edge",
      gate: {
        $type: "all",
        predicates: [
          { $type: "compareState", state: "mancalaMoveRequest", comparandState: "mancalaMoveApplied", comparison: "NotEqual" },
          { $type: "compareState", state: "mancalaStatus", value: 0, comparison: "Equal" },
          { $type: "compareState", state: "mancalaSelectedPit", value: 0, comparison: "GreaterOrEqual" },
          { $type: "compareState", state: "mancalaSelectedPit", value: 12, comparison: "LessOrEqual" },
        ],
      },
      effects: [
        { $type: "setState", state: "mancalaMoveApplied", fromState: "mancalaMoveRequest" },
        { $type: "setState", state: "mancalaTurn", expression: "1 - mancalaTurn" },
      ],
    },
  ],
  state: {
    lattices: [
      {
        $type: "ring",
        name: "mancalaBoard",
        width: 14,
        directions: [
          { name: "forward", x: 1, y: 0, z: 0 },
          { name: "backward", x: -1, y: 0, z: 0 },
        ],
      },
    ],
    world: [
      { name: "mancalaBoard", kind: "int", domain: { $type: "cellsOf", topology: "mancalaBoard" } },
      { name: "mancalaTurn", kind: "int", min: 0, max: 1, value: 0 },
      { name: "mancalaSelectedPit", kind: "int", min: -1, max: 13, value: -1 },
      { name: "mancalaMoveRequest", kind: "int", nonNegative: true, value: 0 },
      { name: "mancalaMoveApplied", kind: "int", nonNegative: true, value: 0 },
      { name: "mancalaStatus", kind: "int", min: 0, max: 3, value: 0 },
      { name: "mancalaPitSeeds", kind: "int", nonNegative: true, value: 0 },
    ],
  },
};

export const HEXLINES_WORLD = {
  rules: [
    {
      name: "hex-place-stone",
      mode: "Edge",
      gate: {
        $type: "all",
        predicates: [
          { $type: "compareState", state: "hexMoveRequest", comparandState: "hexMoveApplied", comparison: "NotEqual" },
          { $type: "compareState", state: "hexWinner", value: 0, comparison: "Equal" },
        ],
      },
      effects: [
        { $type: "setState", state: "hexBoard", fromState: "hexTurn", key: "$cell:hexMoveCell:$value" },
        { $type: "setState", state: "hexMoveApplied", fromState: "hexMoveRequest" },
        { $type: "setState", state: "hexTurn", expression: "3 - hexTurn" },
      ],
    },
  ],
  state: {
    lattices: [
      {
        $type: "hex",
        name: "hexBoard",
        radius: 3,
        directions: [
          { name: "E", x: 1, y: 0, z: 0 },
          { name: "W", x: -1, y: 0, z: 0 },
          { name: "NE", x: 0, y: 1, z: 0 },
          { name: "SW", x: 0, y: -1, z: 0 },
          { name: "NW", x: -1, y: 1, z: 0 },
          { name: "SE", x: 1, y: -1, z: 0 },
        ],
      },
    ],
    world: [
      { name: "hexBoard", kind: "int", domain: { $type: "cellsOf", topology: "hexBoard" } },
      { name: "hexTurn", kind: "int", min: 1, max: 2, value: 1 },
      { name: "hexMoveCell", kind: "int", min: -1, max: 36, value: -1 },
      { name: "hexMoveRequest", kind: "int", nonNegative: true, value: 0 },
      { name: "hexMoveApplied", kind: "int", nonNegative: true, value: 0 },
      { name: "hexWinner", kind: "int", min: 0, max: 3, value: 0 },
    ],
  },
};

export const CHESS_WORLD = {
  rules: [],
  state: {
    lattices: [
      {
        $type: "grid",
        name: "chessBoard",
        width: 8,
        depth: 8,
        directions: [
          { name: "N", x: 0, y: -1, z: 0 },
          { name: "S", x: 0, y: 1, z: 0 },
          { name: "E", x: 1, y: 0, z: 0 },
          { name: "W", x: -1, y: 0, z: 0 },
          { name: "NE", x: 1, y: -1, z: 0 },
          { name: "NW", x: -1, y: -1, z: 0 },
          { name: "SE", x: 1, y: 1, z: 0 },
          { name: "SW", x: -1, y: 1, z: 0 },
        ],
      },
    ],
    world: [
      { name: "chessBoard", kind: "int", domain: { $type: "cellsOf", topology: "chessBoard" } },
      { name: "chessTurn", kind: "int", min: 1, max: 2, value: 1 },
      { name: "chessMoveCount", kind: "int", nonNegative: true, value: 0 },
    ],
  },
};

export const AVAILABLE_PRESETS: WorldCatalogItem[] = [
  {
    id: "tictactoe",
    name: "Qubic 4×4×4 Tic-Tac-Toe",
    subtitle: "3D Box lattice with 13 directional ray shifts & 76 winning lines",
    category: "Lattice CAD",
    world: TIC_TAC_TOE_WORLD,
  },
  {
    id: "mancala",
    name: "Mancala Pit Distribute",
    subtitle: "14-cell cyclic ring topology with counter sowing & pits",
    category: "Ring",
    world: MANCALA_WORLD,
  },
  {
    id: "hexlines",
    name: "Hexagonal Lattice Lines",
    subtitle: "Axial (q, r) hexagonal disk lattice with 6 compass rays",
    category: "Strategy",
    world: HEXLINES_WORLD,
  },
  {
    id: "chess",
    name: "8×8 Orthogonal Chess Board",
    subtitle: "Rank & file orthogonal 2D grid with piece states",
    category: "Board",
    world: CHESS_WORLD,
  },
];
