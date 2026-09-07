/**
 * Where "Reveal in JSON" points a selected cell at: the row's own `cells[]` entry when the cell
 * carries an authored value, else the row object itself — so revealing an un-authored,
 * default-value cell still lands somewhere sensible in the document rather than nowhere.
 */
import type { JsonPath } from "../document/jsonPath";
import { listStateRows } from "./documentTools";

export function cellJsonPath(document: unknown, rowName: string, ordinal: number): JsonPath | null {
  const rows = listStateRows(document);
  const rowIndex = rows.findIndex((row) => row.name === rowName);
  if (rowIndex < 0) {
    return null;
  }
  const row = rows[rowIndex];
  const cellIndex = row.cells?.findIndex((cell) => cell.key === String(ordinal)) ?? -1;
  return (cellIndex >= 0)
    ? ["state", "world", rowIndex, "cells", cellIndex]
    : ["state", "world", rowIndex];
}
