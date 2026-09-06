import React, { useEffect, useId, useRef, useState } from "react";
import { Button, Group, Text } from "@mantine/core";
import type { SceneCell } from "../../authoring/sceneProjection";
import type { CellReference } from "../../authoring/documentTools";
import { appearanceFor, type ValueAppearance } from "../../authoring/presentation";

export interface UniversalTopologyViewProps {
  cells: SceneCell[];
  values: Record<number, number>;
  empty: number;
  selection: Set<number>;
  highlights: Set<number>;
  bindings?: Record<string, ValueAppearance>;
  hex: boolean;
  onSelect: (ref: CellReference, additive: boolean) => void;
}
/** Accessible, paged view of the same document addresses used by the spatial renderer. */
function UniversalTopologyView({ cells, values, empty, selection, highlights, bindings, hex, onSelect }: UniversalTopologyViewProps) {
  const helpId = useId();
  const firstSelected = cells.findIndex(cell => selection.has(cell.ref.index));
  const [page, setPage] = useState(Math.max(0, Math.floor(firstSelected / 144)));
  const [focused, setFocused] = useState(firstSelected < 0 ? cells[0]?.ref.index : cells[firstSelected].ref.index);
  const buttons = useRef(new Map<number, HTMLButtonElement>());
  const pageCount = Math.max(1, Math.ceil(cells.length / 144)), currentPage = Math.min(page, pageCount - 1);
  const visible = cells.slice(currentPage * 144, (currentPage + 1) * 144);
  // Address selection in the inspector also reveals the appropriate page.
  useEffect(() => { if (firstSelected >= 0) setPage(Math.floor(firstSelected / 144)); }, [firstSelected]);
  const columns = Math.min(12, Math.max(1, new Set(visible.map(c => c.coordinate.x)).size));
  const spatialHex = hex && pageCount === 1;
  const minX = Math.min(...visible.map(c => 2 * c.coordinate.x - c.coordinate.y));
  const minY = Math.min(...visible.map(c => c.coordinate.y));
  const hexColumns = Math.max(...visible.map(c => 2 * c.coordinate.x - c.coordinate.y)) - minX + 2;
  const focusIndex = visible.some(c => c.ref.index === focused) ? focused : visible[0]?.ref.index;
  return <div className="studio-plan-view">
    <div className="studio-board-surface">
      <div role="group" aria-label="Topology cells" aria-describedby={helpId} className="studio-cell-grid" data-hex={spatialHex || undefined}
        style={{ gridTemplateColumns: spatialHex ? "repeat(" + hexColumns + ", 24px)" : "repeat(" + columns + ", minmax(44px, 1fr))" }}>
        {visible.map((cell, position) => {
          const { index } = cell.ref, c = cell.coordinate;
          const appearance = appearanceFor(values[index] ?? empty, bindings);
          return <button key={index} type="button" className="studio-cell" aria-pressed={selection.has(index)}
            style={{ color: appearance.color, ...(spatialHex ? { gridColumn: (2 * c.x - c.y - minX + 1) + " / span 2", gridRow: c.y - minY + 1, aspectRatio: "1", minHeight: 48 } : {}) }}
            data-highlighted={highlights.has(index) || undefined}
            ref={node => { if (node) buttons.current.set(index, node); else buttons.current.delete(index); }}
            tabIndex={focusIndex === index ? 0 : -1}
            aria-label={"Cell " + index + ", x " + c.x + ", y " + c.y + ", z " + c.z + ", value " + appearance.label}
            onFocus={() => setFocused(index)} onClick={e => onSelect(cell.ref, e.shiftKey || e.ctrlKey || e.metaKey)}
            onKeyDown={event => {
              const delta: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columns, ArrowDown: columns, Home: -position, End: visible.length - position - 1 };
              if (!(event.key in delta)) return;
              event.preventDefault();
              let next = visible[Math.max(0, Math.min(visible.length - 1, position + delta[event.key]))];
              if (spatialHex && event.key.startsWith("Arrow")) {
                const dx = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
                const dy = event.key === "ArrowUp" ? -1 : event.key === "ArrowDown" ? 1 : 0;
                const candidates = visible.filter(other => dx ? ((2 * other.coordinate.x - other.coordinate.y) - (2 * c.x - c.y)) * dx > 0 && other.coordinate.y === c.y : (other.coordinate.y - c.y) * dy > 0);
                next = candidates.sort((a, b) => (Math.abs(a.coordinate.y - c.y) * 100 + Math.abs(2 * a.coordinate.x - a.coordinate.y - (2 * c.x - c.y))) - (Math.abs(b.coordinate.y - c.y) * 100 + Math.abs(2 * b.coordinate.x - b.coordinate.y - (2 * c.x - c.y))))[0] ?? cell;
              }
              buttons.current.get(next.ref.index)?.focus();
            }}>
            <span className="studio-cell-index">{index}</span><span className="studio-cell-value" aria-hidden="true">{appearance.label}</span>
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
