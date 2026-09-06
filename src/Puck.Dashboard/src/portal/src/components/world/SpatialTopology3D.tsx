import React, { useMemo, useState } from "react";
import { Canvas } from "@react-three/fiber";
import { OrbitControls, Text as Text3D } from "@react-three/drei";
import { Box, Group, Badge, Text } from "@mantine/core";
import * as THREE from "three";
import { TopologyDefinition, getTopologyCoordinates } from "../../engine/evaluator";

export interface SpatialTopology3DProps {
  topology: TopologyDefinition;
  cellValues: Record<number, number>;
  selectedCell?: number | null;
  onSelectCell?: (cellIdx: number) => void;
  hoveredMask?: bigint | null;
  activeWinningRay?: { name: string; cells: number[] } | null;
}

interface Cell3DProps {
  index: number;
  coord: { x: number; y: number; z: number };
  value: number;
  isSelected: boolean;
  isMaskHighlighted: boolean;
  isWinningCell: boolean;
  onSelect: (idx: number) => void;
  onHover: (idx: number | null) => void;
}

const Cell3D: React.FC<Cell3DProps> = ({
  index,
  coord,
  value,
  isSelected,
  isMaskHighlighted,
  isWinningCell,
  onSelect,
  onHover,
}) => {
  const [hovered, setHovered] = useState(false);

  // Position in 3D: spread out in X, Y, Z space
  // Note: Y is vertical in Three.js, so layer z maps to Y height
  const posX = (coord.x - 1.5) * 1.35;
  const posY = (coord.z - 1.5) * 1.6; // Stacking layers vertically
  const posZ = (coord.y - 1.5) * 1.35;

  // Colors based on bds-theorem triadic palette
  const coralColor = "#f2879a"; // Player 1
  const jadeColor = "#4fd0b4";  // Player 2
  const idleColor = "#363145";  // Neutral Ground border
  const hoverColor = "#d15d78";

  const color = useMemo(() => {
    if (isWinningCell) return "#ffd166"; // Gold winning ray
    if (value === 1) return coralColor;
    if (value === 2) return jadeColor;
    if (isSelected) return coralColor;
    if (isMaskHighlighted) return "#a79cf2";
    if (hovered) return hoverColor;
    return idleColor;
  }, [value, isSelected, isMaskHighlighted, isWinningCell, hovered]);

  return (
    <group position={[posX, posY, posZ]}>
      {/* Clickable bounding target */}
      <mesh
        onClick={(e) => {
          e.stopPropagation();
          onSelect(index);
        }}
        onPointerOver={(e) => {
          e.stopPropagation();
          setHovered(true);
          onHover(index);
        }}
        onPointerOut={() => {
          setHovered(false);
          onHover(null);
        }}
      >
        <boxGeometry args={[0.95, 0.22, 0.95]} />
        <meshStandardMaterial
          color={color}
          transparent
          opacity={value > 0 || isSelected || isWinningCell ? 0.9 : hovered || isMaskHighlighted ? 0.65 : 0.25}
          roughness={0.2}
          metalness={0.3}
        />
      </mesh>

      {/* Wireframe border */}
      <lineSegments>
        <edgesGeometry args={[new THREE.BoxGeometry(0.95, 0.22, 0.95)]} />
        <lineBasicMaterial
          color={isWinningCell ? "#ffd166" : isSelected ? coralColor : isMaskHighlighted ? "#a79cf2" : "#847d93"}
          linewidth={isSelected || isWinningCell ? 2 : 1}
        />
      </lineSegments>

      {/* Mark representation inside cell */}
      {value === 1 && (
        <mesh position={[0, 0.28, 0]}>
          <sphereGeometry args={[0.26, 16, 16]} />
          <meshStandardMaterial color={coralColor} roughness={0.1} metalness={0.2} />
        </mesh>
      )}

      {value === 2 && (
        <mesh position={[0, 0.28, 0]} rotation={[Math.PI / 2, 0, 0]}>
          <torusGeometry args={[0.22, 0.08, 12, 24]} />
          <meshStandardMaterial color={jadeColor} roughness={0.1} metalness={0.2} />
        </mesh>
      )}

      {/* Cell number label */}
      <Text3D
        position={[0, 0.13, 0]}
        rotation={[-Math.PI / 2, 0, 0]}
        fontSize={0.22}
        color={value > 0 ? "#ffffff" : "#b5aec3"}
        anchorX="center"
        anchorY="middle"
      >
        {value === 1 ? "X" : value === 2 ? "O" : String(index)}
      </Text3D>
    </group>
  );
};

export const SpatialTopology3D: React.FC<SpatialTopology3DProps> = ({
  topology,
  cellValues,
  selectedCell,
  onSelectCell,
  hoveredMask,
  activeWinningRay,
}) => {
  const [hoveredCellIdx, setHoveredCellIdx] = useState<number | null>(null);
  const coords = useMemo(() => getTopologyCoordinates(topology), [topology]);

  const layers = topology.dimensions?.z ?? topology.layers ?? (topology.$type === "box" ? 4 : 1);

  // Check if a cell is part of the active winning ray
  const winningCellSet = useMemo(() => {
    return new Set(activeWinningRay?.cells ?? []);
  }, [activeWinningRay]);

  return (
    <Box style={{ position: "relative", width: "100%", height: 460, borderRadius: 12, overflow: "hidden", background: "var(--code-bg)" }}>
      {/* 3D Canvas */}
      <Canvas camera={{ position: [5.2, 5.8, 6.8], fov: 42 }}>
        <ambientLight intensity={0.7} />
        <pointLight position={[10, 15, 10]} intensity={1.2} />
        <pointLight position={[-10, -10, -10]} intensity={0.4} />

        <OrbitControls enableDamping dampingFactor={0.06} makeDefault />

        {/* Render horizontal layer plates */}
        {Array.from({ length: layers }).map((_, l) => {
          const y = (l - 1.5) * 1.6;
          return (
            <group key={`layer-plate-${l}`} position={[0, y - 0.13, 0]}>
              <mesh rotation={[-Math.PI / 2, 0, 0]}>
                <planeGeometry args={[5.8, 5.8]} />
                <meshBasicMaterial color="#262231" transparent opacity={0.35} depthWrite={false} />
              </mesh>
              <Text3D
                position={[-2.7, 0, -2.7]}
                rotation={[-Math.PI / 2, 0, 0]}
                fontSize={0.28}
                color="#847d93"
                anchorX="left"
                anchorY="bottom"
              >
                {`Layer Z = ${l}`}
              </Text3D>
            </group>
          );
        })}

        {/* Render 3D Cells */}
        {coords.map((c, idx) => {
          const val = cellValues[idx] ?? 0;
          const isSelected = selectedCell === idx;
          const isMasked = hoveredMask !== null && hoveredMask !== undefined
            ? ((hoveredMask & (1n << BigInt(idx))) !== 0n)
            : false;
          const isWinning = winningCellSet.has(idx);

          return (
            <Cell3D
              key={idx}
              index={idx}
              coord={c}
              value={val}
              isSelected={isSelected}
              isMaskHighlighted={isMasked}
              isWinningCell={isWinning}
              onSelect={(i) => onSelectCell?.(i)}
              onHover={setHoveredCellIdx}
            />
          );
        })}
      </Canvas>

      {/* Floating HUD Controls */}
      <Group justify="space-between" style={{ position: "absolute", top: 12, left: 12, right: 12, pointerEvents: "none" }}>
        <Group gap="xs" style={{ pointerEvents: "auto" }}>
          <Badge color="coral" variant="filled" size="sm" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
            3D Spatial Orbit CAD
          </Badge>
          <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-soft)" }}>
            Drag to Rotate • Scroll to Zoom
          </Text>
        </Group>

        <Group gap="xs" style={{ pointerEvents: "auto" }}>
          {hoveredCellIdx !== null && coords[hoveredCellIdx] && (
            <Badge color="gray" variant="light" size="sm" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
              Cell #{hoveredCellIdx} (x:{coords[hoveredCellIdx].x}, y:{coords[hoveredCellIdx].y}, z:{coords[hoveredCellIdx].z})
            </Badge>
          )}

          {activeWinningRay && (
            <Badge color="yellow" variant="filled" size="sm" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
              Winning Ray: {activeWinningRay.name}
            </Badge>
          )}
        </Group>
      </Group>
    </Box>
  );
};

export default SpatialTopology3D;
