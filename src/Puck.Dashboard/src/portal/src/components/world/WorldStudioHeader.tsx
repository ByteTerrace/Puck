import { Badge, Button, Group, Menu, Paper, Select, Text } from "@mantine/core";
import { RiArrowDownSLine, RiArrowLeftLine, RiArrowRightLine, RiFolderOpenLine, RiSaveLine, RiToolsLine } from "@remixicon/react";
import { SimulationContext } from "../../context/SimulationContext";
import { AVAILABLE_PRESETS } from "../../catalog/worldCatalog";
import type { WorldMetadata } from "../../clients/worldStorageClient";
export type StudioModalType = "vault" | "save" | "topology" | "schema" | "rule" | "hud" | null;
export interface WorldStudioHeaderProps {
  selectedPresetId: string;
  draftDirty?: boolean;
  onSelectPreset: (id: string, metadata: Partial<WorldMetadata>) => void;
  onOpenModal: (modal: StudioModalType) => void;
}
export function WorldStudioHeader({ selectedPresetId, draftDirty = false, onSelectPreset, onOpenModal }: WorldStudioHeaderProps) {
  const actor = SimulationContext.useActorRef();
  const dirty = SimulationContext.useSelector(s => s.context.isDirty) || draftDirty;
  const canUndo = SimulationContext.useSelector(s => s.context.documentIndex > 0);
  const canRedo = SimulationContext.useSelector(s => s.context.documentIndex < s.context.documentHistory.length - 1);
  const revision = SimulationContext.useSelector(s => s.context.documentHistory[s.context.documentIndex].label);
  return <Paper p="sm" radius="md" withBorder className="studio-document-toolbar">
    <Group justify="space-between" gap="sm">
      <div><Text className="kicker">Puck authoring studio</Text><Text component="h1" m={0} fw={600} size="lg">World workspace</Text></div>
      <Group gap="xs">
        <Select aria-label="World Preset" size="xs" w={255} value={AVAILABLE_PRESETS.some(p => p.id === selectedPresetId) ? selectedPresetId : null} placeholder="Custom document" allowDeselect={false}
          data={AVAILABLE_PRESETS.map(p => ({ value: p.id, label: p.name }))} onChange={id => {
            const item = AVAILABLE_PRESETS.find(p => p.id === id);
            if (item) onSelectPreset(item.id, { name: item.name, author: "ByteTerrace Catalog", description: item.subtitle, category: item.category, visibility: "public" });
          }} />
        <Button size="xs" variant="default" leftSection={<RiFolderOpenLine size={15} />} onClick={() => onOpenModal("vault")}>Local library</Button>
        <Button size="xs" variant={dirty ? "filled" : "default"} leftSection={<RiSaveLine size={15} />} onClick={() => onOpenModal("save")}>Save locally{dirty ? " *" : ""}</Button>
        <Menu position="bottom-end" width={240}>
          <Menu.Target><Button size="xs" variant="default" leftSection={<RiToolsLine size={15} />} rightSection={<RiArrowDownSLine size={14} />}>Design</Button></Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>Document structure</Menu.Label>
            <Menu.Item onClick={() => onOpenModal("topology")}>Topology CAD Designer</Menu.Item>
            <Menu.Item onClick={() => onOpenModal("schema")}>State Schema & Domains</Menu.Item>
            <Menu.Item onClick={() => onOpenModal("rule")}>Reactive Rule Composer</Menu.Item>
            <Menu.Item onClick={() => onOpenModal("hud")}>Player HUD & Theme</Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </Group>
    </Group>
    <Group gap="xs" mt="sm">
      <Button size="compact-xs" variant="default" leftSection={<RiArrowLeftLine size={14} />} disabled={!canUndo || draftDirty} onClick={() => actor.send({ type: "DOCUMENT_UNDO" })}>Undo edit</Button>
      <Button size="compact-xs" variant="default" rightSection={<RiArrowRightLine size={14} />} disabled={!canRedo || draftDirty} onClick={() => actor.send({ type: "DOCUMENT_REDO" })}>Redo edit</Button>
      <Text size="xs" c="dimmed" role="status" style={{ flex: 1 }}>{revision}</Text>
      <Badge variant="light" color={dirty ? "yellow" : "teal"}>{dirty ? "Unsaved changes" : "No unsaved changes"}</Badge>
    </Group>
  </Paper>;
}
export default WorldStudioHeader;
