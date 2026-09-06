import { getTopologyCoordinates, type TopologyDefinition } from "./evaluator";
import type { TickSnapshot } from "./replayTape";
import type { StateRowDefinition } from "../authoring/documentTools";

/** Demo request conventions live at the execution seam, never in selection or rendering. */
export function previewCellAction(snapshot: TickSnapshot, topology: TopologyDefinition | undefined, rows: StateRowDefinition[], index: number) {
  const prefix = Object.hasOwn(snapshot.state, "tttMoveRequest") ? "ttt" : Object.hasOwn(snapshot.state, "hexMoveRequest") ? "hex" : null;
  if (!prefix) throw new Error("No cell input adapter for this document. Use preview registers or dispatch an action.");
  const board = rows.find(row => row.name === prefix + "Board" && row.domain?.topology === topology?.name);
  if (!topology || !board || !Number.isInteger(index) || index < 0 || index >= getTopologyCoordinates(topology).length) throw new Error("This selection does not belong to the demo input domain.");
  if ((snapshot.boardCells[board.name]?.[index] ?? 0) !== 0 || (snapshot.state[prefix + "Winner"] ?? 0) !== 0) throw new Error("The preview adapter refused this move: occupied cell or completed game.");
  return { [prefix + "MoveCell"]: index, [prefix + "MoveRequest"]: Number(snapshot.state[prefix + "MoveRequest"]) + 1 };
}
