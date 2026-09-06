import React, { useId, useMemo, useRef, useState } from "react";
import { Badge, Button, Card, Group, Select, Stack, Text } from "@mantine/core";
import { getTopologyCoordinates, TopologyDefinition } from "../../engine/evaluator";
export type { TopologyDefinition };
export interface UniversalTopologyViewProps {
  topology: TopologyDefinition;
  cellValues?: Record<number, number>;
  selectedCell?: number | null;
  onSelectCell?: (cellIndex: number) => void;
  activeDirection?: {
    name: string;
    x: number;
    y: number;
    z: number;
  } | null;
  hoveredMask?: bigint | null;
  activeWinningRay?: {
    name: string;
    cells: number[];
  } | null;
  ghostCell?: number | null;
  ghostPlayer?: number;
  onHoverCell?: (cellIndex: number | null) => void;
}
const label = (value: number) => value === 1 ? "X" : value === 2 ? "O" : value === 0 ? "Empty" : String(value);
export const UniversalTopologyView: React.FC<UniversalTopologyViewProps> = ({ topology, cellValues = {}, onSelectCell, hoveredMask, activeWinningRay }) => {
  const helpId = useId();
  const coordinates = useMemo(() => {
    try {
      return getTopologyCoordinates(topology);
    }
    catch {
      return [];
    }
  }, [topology]);
  const slices = useMemo(() => [...new Set(coordinates.map(c => c.z))].sort((a, b) => a - b), [coordinates]);
  const [slice, setSlice] = useState(0);
  const [page, setPage] = useState(0);
  const [inspected, setInspected] = useState<number | null>(null);
  const [focused, setFocused] = useState(0);
  const buttons = useRef(new Map<number, HTMLButtonElement>());
  const z = slices.includes(slice) ? slice : slices[0];
  const cells = useMemo(() => coordinates.map((c, index) => ({ ...c, index })).filter(c => c.z === z), [coordinates, z]);
  const pageCount = Math.max(1, Math.ceil(cells.length / 144));
  const currentPage = Math.min(page, pageCount - 1);
  const visible = cells.slice(currentPage * 144, (currentPage + 1) * 144);
  const columns = Math.min(12, topology.$type === "ring" ? 8 : Math.max(1, new Set(visible.map(c => c.x)).size));
  const spatialHex = topology.$type === "hex" && pageCount === 1;
  const minHexX = Math.min(...visible.map(c => 2 * c.x - c.y));
  const minHexY = Math.min(...visible.map(c => c.y));
  const hexColumns = Math.max(...visible.map(c => 2 * c.x - c.y)) - minHexX + 2;
  const focusIndex = visible.some(c => c.index === focused) ? focused : visible[0]?.index;
  const inspectedCell = inspected === null ? undefined : coordinates[inspected];
  return <Card
    withBorder
    radius="md"
    p="md"
    className="studio-board">
    <Group
      justify="space-between"
      mb="md">
      <div><Text
        component="h2"
        fw={650}
        size="lg"
        m={0}>{topology.name}</Text><Text
          size="sm"
          c="var(--ink-soft)">Board preview</Text></div>
      <Badge
        variant="light"
        color="jade">{coordinates.length} cells · {topology.$type}</Badge>
    </Group>
    <Group
      justify="space-between"
      mb="sm"
      mih={50}>
      <Text
        size="sm"
        c="var(--ink-soft)">{slices.length > 1 ? "Explore one layer at a time" : topology.$type === "hex" ? "Native hex cell order" : "Select a cell to submit a demo move"}</Text>
      {slices.length > 1 && <Select
        label="Layer"
        value={String(z)}
        onChange={value => { setSlice(Number(value)); setPage(0); setInspected(null); }}
        data={slices.map(value => ({ value: String(value), label: "z = " + value }))}
        w={120}
        allowDeselect={false} />}
    </Group>
    <div
      className="studio-board-surface"
      onMouseLeave={() => setInspected(null)}>
      <div
        role="group"
        aria-label={topology.name + " cells"}
        aria-describedby={helpId}
        className="studio-cell-grid"
        data-hex={spatialHex || undefined}
        style={{ gridTemplateColumns: spatialHex ? "repeat(" + hexColumns + ", 24px)" : "repeat(" + columns + ", minmax(44px, 1fr))" }}>
        {visible.map((c, position) => {
          const value = cellValues[c.index] ?? 0;
          const highlighted = activeWinningRay?.cells.includes(c.index) || (c.index < 64 && hoveredMask != null && (hoveredMask & (1n << BigInt(c.index))) !== 0n);
          return <button
            key={c.index}
            type="button"
            className="studio-cell"
            style={spatialHex ? { gridColumn: (2 * c.x - c.y - minHexX + 1) + " / span 2", gridRow: c.y - minHexY + 1, aspectRatio: "1", minHeight: 48 } : undefined}
            data-value={value}
            data-highlighted={highlighted || undefined}
            ref={node => {
              if(node)
                buttons.current.set(c.index, node);
              else
                buttons.current.delete(c.index);
            }}
            tabIndex={focusIndex === c.index ? 0 : -1}
            aria-label={"Cell " + c.index + ", x " + c.x + ", y " + c.y + ", z " + c.z + ", " + label(value)}
            onFocus={() => { setFocused(c.index); setInspected(c.index); }}
            onMouseEnter={() => setInspected(c.index)}
            onClick={() => onSelectCell?.(c.index)}
            onKeyDown={event => {
              const delta: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columns, ArrowDown: columns, Home: -position, End: visible.length - position - 1 };
              if(event.key in delta) {
                event.preventDefault();
                let next = visible[Math.max(0, Math.min(visible.length - 1, position + delta[event.key]))];
                if(spatialHex && event.key.startsWith("Arrow")) {
                  const dx = event.key === "ArrowLeft" ? -1 : event.key === "ArrowRight" ? 1 : 0;
                  const dy = event.key === "ArrowUp" ? -1 : event.key === "ArrowDown" ? 1 : 0;
                  const candidates = visible.filter(other => dx ? ((2 * other.x - other.y) - (2 * c.x - c.y)) * dx > 0 && other.y === c.y : (other.y - c.y) * dy > 0);
                  next = candidates.sort((a, b) => (Math.abs(a.y - c.y) * 100 + Math.abs(2 * a.x - a.y - (2 * c.x - c.y))) - (Math.abs(b.y - c.y) * 100 + Math.abs(2 * b.x - b.y - (2 * c.x - c.y))))[0] ?? c;
                }
                buttons.current.get(next.index)?.focus();
              }
            }}>
            <span
              className="studio-cell-index">{c.index}</span><span
                className="studio-cell-value"
                aria-hidden="true">{value === 0 ? "·" : label(value)}</span>
          </button>;
        })}
      </div>
      {!coordinates.length && <Text>Geometry is unavailable for this topology in offline preview.</Text>}
    </div>
    {pageCount > 1 && <Group
      justify="space-between"
      mt="sm"><Button
        variant="default"
        disabled={!currentPage}
        onClick={() => setPage(currentPage - 1)}>Previous cells</Button><Text
          size="sm">{currentPage + 1} / {pageCount}</Text><Button
            variant="default"
            disabled={currentPage + 1 === pageCount}
            onClick={() => setPage(currentPage + 1)}>Next cells</Button></Group>}
    <Stack
      gap={4}
      className="studio-cell-inspector"
      mt="md">
      <Text
        size="sm"
        fw={600}>{inspectedCell ? "Cell " + inspected + " · " + label(cellValues[inspected!] ?? 0) + " · (" + inspectedCell.x + ", " + inspectedCell.y + ", " + inspectedCell.z + ")" : "Point to or focus a cell to inspect it"}</Text>
      <Text
        id={helpId}
        size="xs"
        c="var(--ink-soft)">Arrow keys move focus. Enter or Space submits a move. Hover never changes preview state.</Text>
    </Stack>
  </Card>;
};
export default React.memo(UniversalTopologyView);
