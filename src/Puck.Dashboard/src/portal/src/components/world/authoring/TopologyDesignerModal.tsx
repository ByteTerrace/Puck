import React, { useState } from "react";
import {
  Modal,
  TextInput,
  NumberInput,
  SegmentedControl,
  Button,
  Group,
  Stack,
  Text,
  Paper,
  ActionIcon,
  Divider,
  SimpleGrid,
} from "@mantine/core";
import {
  RiGridFill,
  RiAddLine,
  RiDeleteBinLine,
  RiCheckLine,
} from "@remixicon/react";
import { TopologyDefinition } from "../../../engine/evaluator";

export interface TopologyDesignerModalProps {
  opened: boolean;
  onClose: () => void;
  onSaveTopology: (topology: TopologyDefinition) => void;
  existingTopologies?: TopologyDefinition[];
}

export const TopologyDesignerModal: React.FC<TopologyDesignerModalProps> = ({
  opened,
  onClose,
  onSaveTopology,
  existingTopologies = [],
}) => {
  const [name, setName] = useState(`topology-${existingTopologies.length + 1}`);
  const [type, setType] = useState<"box" | "grid" | "ring" | "hex">("box");
  const [width, setWidth] = useState<number>(4);
  const [depth, setDepth] = useState<number>(4);
  const [layers, setLayers] = useState<number>(4);
  const [layerHeight, setLayerHeight] = useState<number>(1);
  const [cellSize, setCellSize] = useState<number>(1);
  const [radius, setRadius] = useState<number>(3);

  // Directions
  const [directions, setDirections] = useState<
    Array<{ name: string; x: number; y: number; z: number; opposite?: string }>
  >([
    { name: "L0", x: 0, y: 0, z: 1, opposite: "L0Back" },
    { name: "L2", x: 0, y: 1, z: 0, opposite: "L2Back" },
    { name: "L8", x: 1, y: 0, z: 0, opposite: "L8Back" },
  ]);

  const handleAddDirection = () => {
    const nextIdx = directions.length;
    setDirections((prev) => [
      ...prev,
      {
        name: `D${nextIdx}`,
        x: 1,
        y: 1,
        z: 0,
        opposite: `D${nextIdx}Back`,
      },
    ]);
  };

  const handleRemoveDirection = (index: number) => {
    setDirections((prev) => prev.filter((_, i) => i !== index));
  };

  const handleSave = () => {
    const cleanName = name.trim() || "customTopology";
    const topo: TopologyDefinition = {
      $type: type,
      name: cleanName,
      cellSize,
      directions,
    };

    if (type === "box") {
      topo.width = width;
      topo.depth = depth;
      topo.layers = layers;
      topo.layerHeight = layerHeight;
      topo.dimensions = { x: width, y: depth, z: layers };
    } else if (type === "grid") {
      topo.width = width;
      topo.depth = depth;
    } else if (type === "ring") {
      topo.width = width; // Ring element count
    } else if (type === "hex") {
      topo.radius = radius;
    }

    onSaveTopology(topo);
    onClose();
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiGridFill size={20} color="var(--accent)" />
          <Text fw={600} style={{ fontFamily: '"Lora", Georgia, serif' }}>
            Lattice & Topology CAD Designer
          </Text>
        </Group>
      }
      size="lg"
      styles={{
        content: { background: "var(--paper-2)", color: "var(--ink)", border: "1px solid var(--rule)" },
        header: { background: "var(--paper-2)", color: "var(--ink)" },
      }}
    >
      <Stack gap="md">
        <Group grow align="flex-start">
          <TextInput
            label="Topology Identifier"
            value={name}
            onChange={(e) => setName(e.currentTarget.value)}
            size="xs"
            styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
            required
          />

          <Stack gap={4}>
            <Text size="xs" fw={500}>
              Lattice Structure ($type)
            </Text>
            <SegmentedControl
              size="xs"
              value={type}
              onChange={(val: any) => setType(val)}
              data={[
                { label: "Box (3D)", value: "box" },
                { label: "Grid (2D)", value: "grid" },
                { label: "Ring (1D)", value: "ring" },
                { label: "Hex (Axial)", value: "hex" },
              ]}
            />
          </Stack>
        </Group>

        {/* Dimension Parameters */}
        <Divider label="Spatial Dimensions" labelPosition="left" />

        {type === "box" && (
          <SimpleGrid cols={{ base: 2, sm: 5 }} spacing="xs">
            <NumberInput
              label="Width (X)"
              value={width}
              onChange={(v) => setWidth(Number(v) || 4)}
              min={1}
              max={16}
              size="xs"
            />
            <NumberInput
              label="Depth (Y)"
              value={depth}
              onChange={(v) => setDepth(Number(v) || 4)}
              min={1}
              max={16}
              size="xs"
            />
            <NumberInput
              label="Layers (Z)"
              value={layers}
              onChange={(v) => setLayers(Number(v) || 4)}
              min={1}
              max={16}
              size="xs"
            />
            <NumberInput
              label="Layer Height"
              value={layerHeight}
              onChange={(v) => setLayerHeight(Number(v) || 1)}
              min={0.5}
              max={5}
              step={0.5}
              size="xs"
            />
            <NumberInput
              label="Cell Size"
              value={cellSize}
              onChange={(v) => setCellSize(Number(v) || 1)}
              min={0.2}
              max={5}
              step={0.1}
              size="xs"
            />
          </SimpleGrid>
        )}

        {type === "grid" && (
          <SimpleGrid cols={2} spacing="xs">
            <NumberInput
              label="Width (Columns)"
              value={width}
              onChange={(v) => setWidth(Number(v) || 8)}
              min={1}
              max={32}
              size="xs"
            />
            <NumberInput
              label="Depth (Rows)"
              value={depth}
              onChange={(v) => setDepth(Number(v) || 8)}
              min={1}
              max={32}
              size="xs"
            />
          </SimpleGrid>
        )}

        {type === "ring" && (
          <NumberInput
            label="Ring Node Count"
            description="Number of cyclic cells around the loop (e.g. 14 for Mancala)"
            value={width}
            onChange={(v) => setWidth(Number(v) || 14)}
            min={3}
            max={64}
            size="xs"
          />
        )}

        {type === "hex" && (
          <NumberInput
            label="Hex Disk Radius"
            description="Axial radius from center (radius 3 = 37 hexagons)"
            value={radius}
            onChange={(v) => setRadius(Number(v) || 3)}
            min={1}
            max={8}
            size="xs"
          />
        )}

        {/* Direction Vectors */}
        <Divider label="Lattice Direction Vectors (Ray Propagation)" labelPosition="left" />

        <Stack gap="xs">
          {directions.map((d, idx) => (
            <Paper key={idx} p="xs" radius="xs" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Group gap="xs" align="center">
                <TextInput
                  size="xs"
                  label="Ray Name"
                  style={{ width: 90 }}
                  value={d.name}
                  onChange={(e) => {
                    const updated = [...directions];
                    updated[idx].name = e.currentTarget.value;
                    setDirections(updated);
                  }}
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                <NumberInput
                  size="xs"
                  label="dx"
                  style={{ width: 65 }}
                  value={d.x}
                  onChange={(v) => {
                    const updated = [...directions];
                    updated[idx].x = Number(v) || 0;
                    setDirections(updated);
                  }}
                />

                <NumberInput
                  size="xs"
                  label="dy"
                  style={{ width: 65 }}
                  value={d.y}
                  onChange={(v) => {
                    const updated = [...directions];
                    updated[idx].y = Number(v) || 0;
                    setDirections(updated);
                  }}
                />

                <NumberInput
                  size="xs"
                  label="dz"
                  style={{ width: 65 }}
                  value={d.z}
                  onChange={(v) => {
                    const updated = [...directions];
                    updated[idx].z = Number(v) || 0;
                    setDirections(updated);
                  }}
                />

                <TextInput
                  size="xs"
                  label="Opposite Name"
                  style={{ flex: 1 }}
                  value={d.opposite ?? ""}
                  onChange={(e) => {
                    const updated = [...directions];
                    updated[idx].opposite = e.currentTarget.value;
                    setDirections(updated);
                  }}
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                <ActionIcon
                  size="sm"
                  color="red"
                  variant="subtle"
                  mt={18}
                  onClick={() => handleRemoveDirection(idx)}
                >
                  <RiDeleteBinLine size={14} />
                </ActionIcon>
              </Group>
            </Paper>
          ))}

          <Button
            size="compact-xs"
            variant="light"
            color="jade"
            leftSection={<RiAddLine size={12} />}
            onClick={handleAddDirection}
            style={{ alignSelf: "flex-start" }}
          >
            Add Direction Vector
          </Button>
        </Stack>

        <Group justify="flex-end" mt="sm">
          <Button variant="subtle" color="gray" size="xs" onClick={onClose}>
            Cancel
          </Button>
          <Button
            color="coral"
            size="xs"
            leftSection={<RiCheckLine size={14} />}
            onClick={handleSave}
          >
            Save Topology
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default TopologyDesignerModal;
