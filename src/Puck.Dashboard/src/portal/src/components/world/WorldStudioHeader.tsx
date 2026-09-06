import React from "react";
import {
  Paper,
  Group,
  Text,
  Badge,
  Select,
  Button,
  Menu,
  Tooltip,
} from "@mantine/core";
import {
  RiRefreshLine,
  RiGamepadLine,
  RiUser3Line,
  RiArrowLeftLine,
  RiArrowRightLine,
  RiCloudLine,
  RiFolderShield2Line,
  RiUploadCloud2Line,
  RiDatabaseLine,
  RiToolsLine,
  RiArrowDownSLine,
  RiCompass3Fill,
  RiAddLine,
  RiPaletteLine,
} from "@remixicon/react";
import { SimulationContext } from "../../context/SimulationContext";
import {
  selectActivePlayer,
  selectTickCount,
  selectTopologies,
  selectSelectedTopologyName,
  selectIsDirty,
  selectCanUndo,
  selectCanRedo,
} from "../../machines/worldSimulationMachine";
import { AVAILABLE_PRESETS } from "../../catalog/worldCatalog";
import { WorldMetadata } from "../../clients/worldStorageClient";

export type StudioModalType = "vault" | "save" | "topology" | "schema" | "rule" | "hud" | null;

export interface WorldStudioHeaderProps {
  selectedPresetId: string;
  draftDirty?: boolean;
  onSelectPreset: (presetId: string, metadata: Partial<WorldMetadata>) => void;
  onOpenModal: (modal: StudioModalType) => void;
}

export const WorldStudioHeader: React.FC<WorldStudioHeaderProps> = ({
  selectedPresetId,
  draftDirty = false,
  onSelectPreset,
  onOpenModal,
}) => {
  const simActor = SimulationContext.useActorRef();

  // High-performance XState slice selectors
  const activePlayer = SimulationContext.useSelector(selectActivePlayer);
  const tickCount = SimulationContext.useSelector(selectTickCount);
  const topologies = SimulationContext.useSelector(selectTopologies);
  const selectedTopologyName = SimulationContext.useSelector(selectSelectedTopologyName);
  const documentDirty = SimulationContext.useSelector(selectIsDirty);
  const isDirty = documentDirty || draftDirty;
  const canUndo = SimulationContext.useSelector(selectCanUndo);
  const canRedo = SimulationContext.useSelector(selectCanRedo);

  const handlePresetChange = (presetId: string | null) => {
    if (!presetId) return;
    const item = AVAILABLE_PRESETS.find((p) => p.id === presetId);
    if (item) {
      onSelectPreset(item.id, {
        name: item.name,
        author: "ByteTerrace Catalog",
        description: item.subtitle,
        category: item.category,
        visibility: "public",
      });
    }
  };

  return (
    <Paper
      p="sm"
      radius="md"
      mb="md"
      style={{ background: "var(--paper-2)", border: "1px solid var(--rule)" }}
    >
      <Group justify="space-between" wrap="wrap" gap="sm">
        {/* Title & Branding */}
        <Group gap="sm">
          <RiGamepadLine size={26} color="var(--accent)" />
          <div>
            <div className="kicker">Puck authoring studio</div>
            <Group gap={8} align="baseline">
              <Text
                component="h1"
                m={0}
                fw={600}
                size="md"
                style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}
              >
                Make a world. Explore its rules.
              </Text>
            </Group>
          </div>
        </Group>

        {/* Preset Selector & Storage Vault Actions */}
        <Group gap="xs" align="flex-end">
          <Select
            size="xs"
            label="World Preset"
            value={selectedPresetId}
            onChange={handlePresetChange}
            data={AVAILABLE_PRESETS.map((p) => ({
              label: `${p.name} (${p.category})`,
              value: p.id,
            }))}
            style={{ width: 220 }}
          />

          {topologies.length > 1 && (
            <Select
              size="xs"
              label="Active Topology"
              value={selectedTopologyName}
              onChange={(val) =>
                val && simActor.send({ type: "SELECT_TOPOLOGY", name: val })
              }
              data={topologies.map((t) => ({
                label: `${t.name} (${t.$type})`,
                value: t.name,
              }))}
              style={{ width: 150 }}
            />
          )}

          <Tooltip label="Open local documents and examples">
            <Button
              size="xs"
              variant="light"
              color="teal"
              leftSection={<RiFolderShield2Line size={15} />}
              onClick={() => onOpenModal("vault")}
            >
              Local library
            </Button>
          </Tooltip>

          <Tooltip
            label={
              isDirty
                ? "Save applied document in this browser"
                : "Save applied document in this browser"
            }
          >
            <Button
              size="xs"
              variant={isDirty ? "filled" : "outline"}
              color={isDirty ? "pink" : "gray"}
              leftSection={<RiUploadCloud2Line size={15} />}
              onClick={() => onOpenModal("save")}
            >
              {isDirty ? "Save locally *" : "Save locally"}
            </Button>
          </Tooltip>

          {/* In-Browser Authoring Dropdown */}
          <Menu shadow="md" width={220} position="bottom-end">
            <Menu.Target>
              <Button
                size="xs"
                variant="light"
                gradient={{ from: "pink", to: "violet" }}
                leftSection={<RiToolsLine size={15} />}
                rightSection={<RiArrowDownSLine size={14} />}
              >
                Design
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>In-Browser World Authoring</Menu.Label>
              <Menu.Item
                leftSection={<RiCompass3Fill size={15} color="var(--accent)" />}
                onClick={() => onOpenModal("topology")}
              >
                Topology CAD Designer
              </Menu.Item>
              <Menu.Item
                leftSection={<RiDatabaseLine size={15} color="var(--accent-2)" />}
                onClick={() => onOpenModal("schema")}
              >
                State Schema & Domains
              </Menu.Item>
              <Menu.Item
                leftSection={<RiAddLine size={15} color="var(--ink)" />}
                onClick={() => onOpenModal("rule")}
              >
                Reactive Rule Composer
              </Menu.Item>
              <Menu.Item
                leftSection={<RiPaletteLine size={15} color="var(--accent)" />}
                onClick={() => onOpenModal("hud")}
              >
                Player HUD & Theme
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>

          {isDirty ? (
            <Badge
              color="yellow"
              variant="light"
              size="sm"
              style={{ height: 30, display: "flex", alignItems: "center" }}
            >
              ● Unsaved Changes
            </Badge>
          ) : (
            <Badge
              color="teal"
              variant="light"
              size="sm"
              leftSection={<RiCloudLine size={12} />}
              style={{ height: 30, display: "flex", alignItems: "center" }}
            >
              No unsaved changes
            </Badge>
          )}
        </Group>

        {/* Simulation & Time-Travel Controls */}
        <Group gap="xs" align="flex-end">
          <Badge
            variant="light"
            color={activePlayer === 1 ? "coral" : "jade"}
            size="md"
            leftSection={<RiUser3Line size={13} />}
          >
            Turn: Player {activePlayer} ({activePlayer === 1 ? "Rose-Coral 'X'" : "Jade 'O'"})
          </Badge>

          <Button
            size="xs"
            variant="default"
            leftSection={<RiArrowLeftLine size={14} />}
            onClick={() => simActor.send({ type: "UNDO" })}
            disabled={!canUndo}
          >
            Undo
          </Button>

          <Badge variant="filled" color="coral" size="md">
            Tick #{tickCount}
          </Badge>

          <Button
            size="xs"
            variant="default"
            rightSection={<RiArrowRightLine size={14} />}
            onClick={() => simActor.send({ type: "REDO" })}
            disabled={!canRedo}
          >
            Redo
          </Button>

          <Button
            size="xs"
            variant="light"
            color="coral"
            leftSection={<RiRefreshLine size={14} />}
            onClick={() => simActor.send({ type: "RESET_WORLD" })}
          >
            Reset
          </Button>
        </Group>
      </Group>
    </Paper>
  );
};

export default WorldStudioHeader;
