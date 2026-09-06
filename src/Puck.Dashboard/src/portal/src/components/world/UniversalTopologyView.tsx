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

export interface TopologyDefinition {
  $type: "grid" | "ring" | "hex" | "box" | "lattice";
  name: string;
  width?: number;
  depth?: number;
  layers?: number;
  radius?: number;
  wrap?: "None" | "X" | "Y" | "Both";
  dimensions?: { x?: number; y?: number; z?: number };
  coordinates?: Array<{ x: number; y: number; z: number }>;
  directions?: Array<{ name: string; x: number; y: number; z: number }>;
}

export interface UniversalTopologyViewProps {
  topology: TopologyDefinition;
  cellValues?: Record<number, number | string>;
  selectedCell?: number | null;
  onSelectCell?: (cellIndex: number) => void;
  activeDirection?: { name: string; x: number; y: number; z: number } | null;
}

export const UniversalTopologyView: React.FC<UniversalTopologyViewProps> = ({
  topology,
  cellValues = {},
  selectedCell,
  onSelectCell,
  activeDirection,
}) => {
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
                        ? "var(--mantine-color-blue-9)"
                        : Number(val) > 0
                        ? "var(--mantine-color-dark-5)"
                        : "var(--mantine-color-dark-7)",
                      border: isSelected
                        ? "2px solid var(--mantine-color-blue-4)"
                        : "1px solid var(--mantine-color-dark-4)",
                      transition: "all 0.15s ease",
                      userSelect: "none",
                    }}
                  >
                    <Text size="xs" c="dimmed" style={{ fontSize: 9 }}>
                      {cellIdx}
                    </Text>
                    <Text fw={700} size="sm" c={Number(val) > 0 ? "cyan.4" : "gray.6"}>
                      {val !== 0 ? String(val) : "·"}
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
            stroke="var(--mantine-color-dark-5)"
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
                  fill={isSelected ? "var(--mantine-color-blue-8)" : "var(--mantine-color-dark-6)"}
                  stroke={isSelected ? "var(--mantine-color-blue-4)" : "var(--mantine-color-dark-4)"}
                  strokeWidth={isSelected ? 2 : 1}
                />
                <text
                  x={x}
                  y={y - 4}
                  textAnchor="middle"
                  fill="gray"
                  fontSize="9"
                  fontFamily="monospace"
                >
                  {i}
                </text>
                <text
                  x={x}
                  y={y + 10}
                  textAnchor="middle"
                  fill={Number(val) > 0 ? "#22d3ee" : "#94a3b8"}
                  fontSize="12"
                  fontWeight="bold"
                >
                  {val !== 0 ? String(val) : "·"}
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
                  fill={isSelected ? "var(--mantine-color-blue-8)" : "var(--mantine-color-dark-6)"}
                  stroke={isSelected ? "var(--mantine-color-blue-4)" : "var(--mantine-color-dark-4)"}
                  strokeWidth={isSelected ? 2 : 1}
                />
                <text
                  x={x}
                  y={y - 3}
                  textAnchor="middle"
                  fill="gray"
                  fontSize="8"
                  fontFamily="monospace"
                >
                  {index}
                </text>
                <text
                  x={x}
                  y={y + 8}
                  textAnchor="middle"
                  fill={Number(val) > 0 ? "#22d3ee" : "#94a3b8"}
                  fontSize="11"
                  fontWeight="bold"
                >
                  {val !== 0 ? String(val) : "·"}
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

              return (
                <Tooltip
                  key={cellIdx}
                  label={`Cell ${cellIdx} (x:${x}, y:${y}, z:${currentZ}) = ${val}`}
                  withArrow
                >
                  <Paper
                    onClick={() => onSelectCell?.(cellIdx)}
                    style={{
                      height: 54,
                      display: "flex",
                      flexDirection: "column",
                      alignItems: "center",
                      justifyContent: "center",
                      cursor: "pointer",
                      borderRadius: 8,
                      background: isSelected
                        ? "var(--mantine-color-blue-9)"
                        : Number(val) > 0
                        ? "var(--mantine-color-dark-5)"
                        : "var(--mantine-color-dark-7)",
                      border: isSelected
                        ? "2px solid var(--mantine-color-blue-4)"
                        : "1px solid var(--mantine-color-dark-4)",
                      transition: "all 0.15s ease",
                      userSelect: "none",
                    }}
                  >
                    <Text size="xs" c="dimmed" style={{ fontSize: 9 }}>
                      #{cellIdx}
                    </Text>
                    <Text fw={700} size="md" c={Number(val) === 1 ? "cyan.4" : Number(val) === 2 ? "amber.4" : "gray.6"}>
                      {Number(val) === 1 ? "X" : Number(val) === 2 ? "O" : val !== 0 ? String(val) : "·"}
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
    <Card withBorder radius="md" p="sm" style={{ background: "var(--mantine-color-dark-8)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiGridFill size={18} color="#22d3ee" />
          <Text fw={700} size="sm">
            {topology.name || "Topology"}
          </Text>
          <Badge variant="outline" color="cyan" size="xs">
            {topology.$type}
          </Badge>
        </Group>

        {activeDirection && (
          <Group gap={4}>
            <RiCompass3Fill size={14} color="#f59e0b" />
            <Text size="xs" c="dimmed">
              Ray: {activeDirection.name} ({activeDirection.x}, {activeDirection.y}, {activeDirection.z})
            </Text>
          </Group>
        )}
      </Group>

      {topology.$type === "grid" && renderGrid()}
      {topology.$type === "ring" && renderRing()}
      {topology.$type === "hex" && renderHex()}
      {(topology.$type === "lattice" || topology.$type === "box") && renderLatticeOrBox()}
    </Card>
  );
};

export default UniversalTopologyView;
