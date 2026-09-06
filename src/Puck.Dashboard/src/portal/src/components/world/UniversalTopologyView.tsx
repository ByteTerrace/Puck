import React, { useState } from "react";
import {
  Box,
  Card,
  Group,
  Stack,
  Text,
  Badge,
  SegmentedControl,
  Tooltip,
  Paper,
} from "@mantine/core";
import { RiGridFill, RiCompass3Fill, RiStackLine } from "@remixicon/react";
import SpatialTopology3D from "./SpatialTopology3D";
import { TopologyDefinition } from "../../engine/evaluator";

export type { TopologyDefinition };

export interface UniversalTopologyViewProps {
  topology: TopologyDefinition;
  cellValues?: Record<number, number>;
  selectedCell?: number | null;
  onSelectCell?: (cellIndex: number) => void;
  activeDirection?: { name: string; x: number; y: number; z: number } | null;
  hoveredMask?: bigint | null;
  activeWinningRay?: { name: string; cells: number[] } | null;
  ghostCell?: number | null;
  ghostPlayer?: number;
  onHoverCell?: (cellIndex: number | null) => void;
}

export const UniversalTopologyView: React.FC<UniversalTopologyViewProps> = ({
  topology,
  cellValues = {},
  selectedCell,
  onSelectCell,
  activeDirection,
  hoveredMask,
  activeWinningRay,
  ghostCell,
  ghostPlayer = 1,
  onHoverCell,
}) => {
  const is3DTopology = topology.$type === "box" || (topology.layers ?? 1) > 1;
  const [viewMode, setViewMode] = useState<"3d" | "2d">(is3DTopology ? "3d" : "2d");
  const [activeSlice, setActiveSlice] = useState<number>(0);

  // 1. Grid Renderer (2D rectangular)
  const renderGrid = () => {
    const width = topology.width ?? 8;
    const depth = topology.depth ?? 8;

    return (
      <Box style={{ overflowX: "auto", padding: 12 }}>
        <div
          style={{
            display: "grid",
            gridTemplateColumns: `repeat(${width}, minmax(40px, 52px))`,
            gap: 6,
            justifyContent: "center",
          }}
        >
          {Array.from({ length: depth }).map((_, z) =>
            Array.from({ length: width }).map((_, x) => {
              const cellIdx = z * width + x;
              const val = cellValues[cellIdx] ?? 0;
              const isSelected = selectedCell === cellIdx;

              return (
                <Tooltip
                  key={cellIdx}
                  label={`Cell ${cellIdx} (x:${x}, z:${z}) = ${val}`}
                  withArrow
                >
                  <Paper
                    onClick={() => onSelectCell?.(cellIdx)}
                    style={{
                      height: 48,
                      display: "flex",
                      flexDirection: "column",
                      alignItems: "center",
                      justifyContent: "center",
                      cursor: "pointer",
                      borderRadius: 6,
                      background: isSelected
                        ? "var(--accent-ink)"
                        : Number(val) > 0
                        ? "var(--quote-bg)"
                        : "var(--paper-2)",
                      border: isSelected
                        ? "2px solid var(--accent)"
                        : "1px solid var(--rule)",
                      transition: "all 0.15s ease",
                      userSelect: "none",
                    }}
                  >
                    <Text size="xs" style={{ fontSize: 9, fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-faint)" }}>
                      {cellIdx}
                    </Text>
                    <Text fw={700} size="sm" style={{ fontFamily: '"JetBrains Mono", monospace', color: Number(val) === 1 ? "var(--accent)" : Number(val) === 2 ? "var(--accent-2)" : "var(--ink-faint)" }}>
                      {Number(val) === 1 ? "X" : Number(val) === 2 ? "O" : val !== 0 ? String(val) : "·"}
                    </Text>
                  </Paper>
                </Tooltip>
              );
            })
          )}
        </div>
      </Box>
    );
  };

  // 2. Ring Renderer (1D cyclic sequence)
  const renderRing = () => {
    const count = topology.width ?? 14;
    const radius = Math.min(180, count * 14);
    const center = radius + 40;

    return (
      <Box style={{ display: "flex", justifyContent: "center", padding: 16 }}>
        <svg width={center * 2} height={center * 2}>
          <circle
            cx={center}
            cy={center}
            r={radius}
            fill="none"
            stroke="var(--rule)"
            strokeWidth="2"
            strokeDasharray="4 4"
          />
          {Array.from({ length: count }).map((_, i) => {
            const angle = (i / count) * 2 * Math.PI - Math.PI / 2;
            const x = center + radius * Math.cos(angle);
            const y = center + radius * Math.sin(angle);
            const val = cellValues[i] ?? 0;
            const isSelected = selectedCell === i;

            return (
              <g
                key={i}
                onClick={() => onSelectCell?.(i)}
                style={{ cursor: "pointer" }}
              >
                <circle
                  cx={x}
                  cy={y}
                  r={20}
                  fill={isSelected ? "var(--accent-ink)" : Number(val) > 0 ? "var(--quote-bg)" : "var(--paper-2)"}
                  stroke={isSelected ? "var(--accent)" : "var(--rule)"}
                  strokeWidth={isSelected ? 2 : 1}
                />
                <text
                  x={x}
                  y={y - 4}
                  textAnchor="middle"
                  fill="var(--ink-faint)"
                  fontSize="9"
                  fontFamily='"JetBrains Mono", monospace'
                >
                  {i}
                </text>
                <text
                  x={x}
                  y={y + 10}
                  textAnchor="middle"
                  fill={Number(val) === 1 ? "var(--accent)" : Number(val) === 2 ? "var(--accent-2)" : "var(--ink-faint)"}
                  fontSize="12"
                  fontWeight="bold"
                  fontFamily='"JetBrains Mono", monospace'
                >
                  {Number(val) === 1 ? "X" : Number(val) === 2 ? "O" : val !== 0 ? String(val) : "·"}
                </text>
              </g>
            );
          })}
        </svg>
      </Box>
    );
  };

  // 3. Hex Renderer (Axial hexagon disk)
  const renderHex = () => {
    const radius = topology.radius ?? 3;
    const hexSize = 22;
    const hexHeight = hexSize * 2;
    const hexWidth = Math.sqrt(3) * hexSize;
    const center = (radius * 2 + 1) * hexSize * 1.5;

    // Generate axial coordinates (q, r) where |q| <= R, |r| <= R, |q + r| <= R
    const hexes: Array<{ q: number; r: number; index: number }> = [];
    let idx = 0;
    for (let q = -radius; q <= radius; q++) {
      const r1 = Math.max(-radius, -q - radius);
      const r2 = Math.min(radius, -q + radius);
      for (let r = r1; r <= r2; r++) {
        hexes.push({ q, r, index: idx++ });
      }
    }

    return (
      <Box style={{ display: "flex", justifyContent: "center", overflowX: "auto", padding: 16 }}>
        <svg width={center * 2} height={center * 2}>
          {hexes.map(({ q, r, index }) => {
            const x = center + hexWidth * (q + r / 2);
            const y = center + hexHeight * (3 / 4) * r;
            const val = cellValues[index] ?? 0;
            const isSelected = selectedCell === index;

            // Compute hexagon vertices
            const points = [0, 1, 2, 3, 4, 5]
              .map((i) => {
                const angle = (Math.PI / 180) * (60 * i - 30);
                return `${x + hexSize * Math.cos(angle)},${y + hexSize * Math.sin(angle)}`;
              })
              .join(" ");

            return (
              <g
                key={index}
                onClick={() => onSelectCell?.(index)}
                style={{ cursor: "pointer" }}
              >
                <polygon
                  points={points}
                  fill={isSelected ? "var(--accent-ink)" : Number(val) > 0 ? "var(--quote-bg)" : "var(--paper-2)"}
                  stroke={isSelected ? "var(--accent)" : "var(--rule)"}
                  strokeWidth={isSelected ? 2 : 1}
                />
                <text
                  x={x}
                  y={y - 3}
                  textAnchor="middle"
                  fill="var(--ink-faint)"
                  fontSize="8"
                  fontFamily='"JetBrains Mono", monospace'
                >
                  {index}
                </text>
                <text
                  x={x}
                  y={y + 8}
                  textAnchor="middle"
                  fill={Number(val) === 1 ? "var(--accent)" : Number(val) === 2 ? "var(--accent-2)" : "var(--ink-faint)"}
                  fontSize="11"
                  fontWeight="bold"
                  fontFamily='"JetBrains Mono", monospace'
                >
                  {Number(val) === 1 ? "X" : Number(val) === 2 ? "O" : val !== 0 ? String(val) : "·"}
                </text>
              </g>
            );
          })}
        </svg>
      </Box>
    );
  };

  // 4. Lattice & Box Renderer (Zero-3D Planar Slice Projection)
  const renderLatticeOrBox = () => {
    // If explicit coordinates exist (e.g. 4x4x4 Qubic tttCube), slice by Z
    const coords = topology.coordinates ?? [];
    const zLayers = Array.from(new Set(coords.map((c) => c.z))).sort((a, b) => a - b);
    const effectiveLayers = zLayers.length > 0 ? zLayers : [0, 1, 2, 3];
    const currentZ = effectiveLayers[activeSlice] ?? effectiveLayers[0];

    // Filter cells in this planar slice
    const sliceCoords = coords
      .map((c, originalIndex) => ({ ...c, originalIndex }))
      .filter((c) => c.z === currentZ);

    const xVals = Array.from(new Set(sliceCoords.map((c) => c.x))).sort((a, b) => a - b);
    const yVals = Array.from(new Set(sliceCoords.map((c) => c.y))).sort((a, b) => a - b);
    const xSize = Math.max(xVals.length, topology.dimensions?.x ?? topology.width ?? 4);

    return (
      <Stack gap="md" p="md">
        <Group justify="space-between" align="center">
          <Group gap="xs">
            <RiStackLine size={18} color="#38bdf8" />
            <Text size="sm" fw={600}>
              Planar Slice (Z = {currentZ})
            </Text>
            <Badge variant="light" color="blue" size="sm">
              Layer {activeSlice + 1} of {effectiveLayers.length}
            </Badge>
          </Group>
          <SegmentedControl
            size="xs"
            value={String(activeSlice)}
            onChange={(val) => setActiveSlice(Number(val))}
            data={effectiveLayers.map((z, idx) => ({
              label: `Slice Z=${z}`,
              value: String(idx),
            }))}
          />
        </Group>

        {/* The 2D Planar Slice Grid */}
        <div
          style={{
            display: "grid",
            gridTemplateColumns: `repeat(${xSize}, minmax(48px, 60px))`,
            gap: 8,
            justifyContent: "center",
            padding: 12,
          }}
        >
          {yVals.map((y) =>
            xVals.map((x) => {
              const item = sliceCoords.find((c) => c.x === x && c.y === y);
              if (!item) return <div key={`empty-${x}-${y}`} />;

              const cellIdx = item.originalIndex;
              const val = cellValues[cellIdx] ?? 0;
              const isSelected = selectedCell === cellIdx;
              const isGhost = ghostCell === cellIdx && Number(val) === 0;

              return (
                <Tooltip
                  key={cellIdx}
                  label={`Cell ${cellIdx} (x:${x}, y:${y}, z:${currentZ}) = ${val}${isGhost ? " [Speculative Ghost Move]" : ""}`}
                  withArrow
                >
                  <Paper
                    onClick={() => onSelectCell?.(cellIdx)}
                    onMouseEnter={() => onHoverCell?.(cellIdx)}
                    onMouseLeave={() => onHoverCell?.(null)}
                    style={{
                      height: 54,
                      display: "flex",
                      flexDirection: "column",
                      alignItems: "center",
                      justifyContent: "center",
                      cursor: "pointer",
                      borderRadius: 6,
                      background: isSelected
                        ? "var(--accent-ink)"
                        : Number(val) > 0
                        ? "var(--quote-bg)"
                        : isGhost
                        ? "var(--accent-ink)"
                        : "var(--paper-2)",
                      border: isSelected
                        ? "2px solid var(--accent)"
                        : isGhost
                        ? "2px dashed var(--accent)"
                        : "1px solid var(--rule)",
                      transition: "all 0.15s ease",
                      userSelect: "none",
                    }}
                  >
                    <Text size="xs" style={{ fontSize: 9, fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-faint)" }}>
                      #{cellIdx}
                    </Text>
                    <Text
                      fw={700}
                      size="md"
                      style={{
                        fontFamily: '"JetBrains Mono", monospace',
                        color:
                          Number(val) === 1
                            ? "var(--accent)"
                            : Number(val) === 2
                            ? "var(--accent-2)"
                            : isGhost
                            ? ghostPlayer === 1
                              ? "var(--accent)"
                              : "var(--accent-2)"
                            : "var(--ink-faint)",
                        opacity: isGhost ? 0.65 : 1,
                      }}
                    >
                      {Number(val) === 1
                        ? "X"
                        : Number(val) === 2
                        ? "O"
                        : isGhost
                        ? ghostPlayer === 1
                          ? "X"
                          : "O"
                        : val !== 0
                        ? String(val)
                        : "·"}
                    </Text>
                  </Paper>
                </Tooltip>
              );
            })
          )}
        </div>
      </Stack>
    );
  };

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiGridFill size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            {topology.name || "Topology"}
          </Text>
          <Badge variant="light" color="coral" size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
            {topology.$type.toUpperCase()}
          </Badge>
        </Group>

        <Group gap="xs">
          {is3DTopology && (
            <SegmentedControl
              size="xs"
              value={viewMode}
              onChange={(val: any) => setViewMode(val)}
              data={[
                { label: "3D Spatial Orbit", value: "3d" },
                { label: "2D Planar Slices", value: "2d" },
              ]}
            />
          )}

          {activeDirection && (
            <Group gap={4}>
              <RiCompass3Fill size={14} color="var(--accent-2)" />
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-soft)" }}>
                Ray: {activeDirection.name} ({activeDirection.x}, {activeDirection.y}, {activeDirection.z})
              </Text>
            </Group>
          )}
        </Group>
      </Group>

      {is3DTopology && viewMode === "3d" ? (
        <SpatialTopology3D
          topology={topology}
          cellValues={cellValues}
          selectedCell={selectedCell}
          onSelectCell={onSelectCell}
          hoveredMask={hoveredMask}
          activeWinningRay={activeWinningRay}
          ghostCell={ghostCell}
          ghostPlayer={ghostPlayer}
          onHoverCell={onHoverCell}
        />
      ) : (
        <>
          {topology.$type === "grid" && renderGrid()}
          {topology.$type === "ring" && renderRing()}
          {topology.$type === "hex" && renderHex()}
          {(topology.$type === "lattice" || topology.$type === "box") && renderLatticeOrBox()}
        </>
      )}
    </Card>
  );
};

export default UniversalTopologyView;
