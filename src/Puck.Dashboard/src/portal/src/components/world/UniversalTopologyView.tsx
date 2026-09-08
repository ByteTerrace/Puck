import React, { useEffect, useId, useRef, useState } from "react";
import { Button, Group, Text } from "@mantine/core";
import type { SceneCell } from "../../authoring/sceneProjection";
import { appearanceFor, type ValueAppearance } from "../../authoring/presentation";

const PAGE_SIZE = 144;

export interface UniversalTopologyViewProps {
  cells: readonly SceneCell[];
  values: ReadonlyMap<number, bigint>;
  empty: bigint;
  selection: ReadonlySet<number>;
  highlights?: ReadonlySet<number>;
  bindings?: Readonly<Record<string, ValueAppearance>>;
  onSelect: (ordinal: number, additive: boolean) => void;
}

/** Accessible, paged view of the same document addresses used by the spatial renderer — a
 * column/row grid built from each `SceneCell`'s own `grid` rank (see `sceneProjection.ts`), never
 * a topology-`$type`-specific layout formula. */
function UniversalTopologyView({ cells, values, empty, selection, highlights, bindings, onSelect }: UniversalTopologyViewProps) {
  const helpId = useId();
  const firstSelected = cells.findIndex((cell) => selection.has(cell.ordinal));
  const [page, setPage] = useState(Math.max(0, Math.floor(firstSelected / PAGE_SIZE)));
  const [focused, setFocused] = useState<number | undefined>(firstSelected >= 0 ? cells[firstSelected].ordinal : cells[0]?.ordinal);
  const buttons = useRef(new Map<number, HTMLButtonElement>());
  const pageCount = Math.max(1, Math.ceil(cells.length / PAGE_SIZE));
  const currentPage = Math.min(page, pageCount - 1);
  const visible = cells.slice(currentPage * PAGE_SIZE, (currentPage + 1) * PAGE_SIZE);
  // Address selection in the inspector also reveals the appropriate page.
  useEffect(() => { if (firstSelected >= 0) setPage(Math.floor(firstSelected / PAGE_SIZE)); }, [firstSelected]);
  const minCol = visible.length ? Math.min(...visible.map((cell) => cell.grid.col)) : 0;
  const minRow = visible.length ? Math.min(...visible.map((cell) => cell.grid.row)) : 0;
  const columns = visible.length ? (Math.max(...visible.map((cell) => cell.grid.col)) - minCol + 1) : 1;
  const focusOrdinal = visible.some((cell) => cell.ordinal === focused) ? focused : visible[0]?.ordinal;
  return <div className="studio-plan-view">
    <div className="studio-board-surface">
      <div role="group" aria-label="Topology cells" aria-describedby={helpId} className="studio-cell-grid"
        style={{ gridTemplateColumns: "repeat(" + columns + ", minmax(44px, 1fr))" }}>
        {visible.map((cell, position) => {
          const { ordinal } = cell;
          const appearance = appearanceFor(values.get(ordinal) ?? empty, bindings);
          return <button key={ordinal} type="button" className="studio-cell" aria-pressed={selection.has(ordinal)}
            style={{ color: appearance.color, gridColumn: (cell.grid.col - minCol + 1), gridRow: (cell.grid.row - minRow + 1) }}
            data-highlighted={highlights?.has(ordinal) || undefined}
            ref={node => { if (node) buttons.current.set(ordinal, node); else buttons.current.delete(ordinal); }}
            tabIndex={focusOrdinal === ordinal ? 0 : -1}
            aria-label={"Cell " + ordinal + ", value " + appearance.label}
            onFocus={() => setFocused(ordinal)} onClick={e => onSelect(ordinal, e.shiftKey || e.ctrlKey || e.metaKey)}
            onKeyDown={event => {
              if (event.key === "Home") { event.preventDefault(); buttons.current.get(visible[0]?.ordinal)?.focus(); return; }
              if (event.key === "End") { event.preventDefault(); buttons.current.get(visible[visible.length - 1]?.ordinal)?.focus(); return; }
              const step: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
              const delta = step[event.key];
              if (!delta) return;
              event.preventDefault();
              const [dCol, dRow] = delta;
              const targetCol = cell.grid.col + dCol, targetRow = cell.grid.row + dRow;
              const next = visible.find(candidate => candidate.grid.col === targetCol && candidate.grid.row === targetRow)
                ?? visible[Math.max(0, Math.min(visible.length - 1, position + dCol + dRow * columns))];
              buttons.current.get(next.ordinal)?.focus();
            }}>
            <span className="studio-cell-index">{ordinal}</span><span className="studio-cell-value" aria-hidden="true">{appearance.label}</span>
          </button>;
        })}
      </div>
      {!cells.length && <Text>No visible cells. Change the layer or visibility filter.</Text>}
    </div>
    {pageCount > 1 && <Group justify="space-between" mt="sm">
      <Button variant="default" disabled={!currentPage} onClick={() => setPage(currentPage - 1)}>Previous cells</Button>
      <Text size="sm">{currentPage + 1} / {pageCount}</Text>
      <Button variant="default" disabled={currentPage + 1 === pageCount} onClick={() => setPage(currentPage + 1)}>Next cells</Button>
    </Group>}
    <Text id={helpId} size="xs" c="dimmed" mt="sm">Arrows move focus. Enter or Space selects. Hold Shift to add or remove a cell.</Text>
  </div>;
}
export default React.memo(UniversalTopologyView);
