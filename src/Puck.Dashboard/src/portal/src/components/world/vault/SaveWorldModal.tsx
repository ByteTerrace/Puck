import React, { useState } from "react";
import {
  Modal,
  TextInput,
  Textarea,
  Select,
  SegmentedControl,
  Button,
  Group,
  Stack,
  Text,
  Alert,
} from "@mantine/core";
import {
  RiCloudLine,
  RiCheckLine,
  RiLockLine,
  RiGlobalLine,
} from "@remixicon/react";
import { WorldMetadata } from "../../../clients/worldStorageClient";

export interface SaveWorldModalProps {
  opened: boolean;
  onClose: () => void;
  onSave: (id: string, metadata: Partial<WorldMetadata>) => Promise<void>;
  currentId?: string;
  defaultName?: string;
}

export const SaveWorldModal: React.FC<SaveWorldModalProps> = ({
  opened,
  onClose,
  onSave,
  currentId = "my-world",
  defaultName = "My Custom World",
}) => {
  const [worldId, setWorldId] = useState(currentId);
  const [name, setName] = useState(defaultName);
  const [author, setAuthor] = useState("You");
  const [description, setDescription] = useState("Authored in Puck World Studio");
  const [category, setCategory] = useState<any>("Strategy");
  const [visibility, setVisibility] = useState<"private" | "public">("private");
  const [isSaving, setIsSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // SafeName validation: lowercase alphanumeric with single dashes, no consecutive dashes
  const safeNameRegex = /^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$/;
  const isSafeNameValid = safeNameRegex.test(worldId.trim().toLowerCase());

  const handleSave = async () => {
    const cleanId = worldId.trim().toLowerCase();
    if (!isSafeNameValid) {
      setError("World ID must be lowercase alphanumeric with dashes (e.g. 'qubic-strategy').");
      return;
    }

    setIsSaving(true);
    setError(null);

    try {
      await onSave(cleanId, {
        name: name.trim() || cleanId,
        author: author.trim() || "You",
        description: description.trim(),
        category,
        visibility,
      });
      setIsSaving(false);
      onClose();
    } catch (err: any) {
      setError(String(err?.message ?? err));
      setIsSaving(false);
    }
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiCloudLine size={18} color="var(--accent)" />
          <Text fw={600} style={{ fontFamily: '"Lora", Georgia, serif' }}>
            Save World to Cloud Storage Substrate
          </Text>
        </Group>
      }
      size="md"
      styles={{
        content: { background: "var(--paper-2)", color: "var(--ink)", border: "1px solid var(--rule)" },
        header: { background: "var(--paper-2)", color: "var(--ink)" },
      }}
    >
      <Stack gap="sm">
        {error && (
          <Alert color="red" variant="light">
            {error}
          </Alert>
        )}

        <TextInput
          label="World Identifier (SafeName)"
          description="Lowercase alphanumeric with hyphens. Used in blob address and Orleans identity."
          placeholder="e.g. hypercube-tactics"
          value={worldId}
          onChange={(e) => setWorldId(e.currentTarget.value.toLowerCase().replace(/[^a-z0-9-]/g, ""))}
          error={!isSafeNameValid && "Invalid SafeName format"}
          size="xs"
          styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
          required
        />

        <TextInput
          label="Display Title"
          placeholder="e.g. Hypercube 4x4 Tactics"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
          size="xs"
          required
        />

        <Group grow>
          <TextInput
            label="Author"
            placeholder="e.g. kittoes0124"
            value={author}
            onChange={(e) => setAuthor(e.currentTarget.value)}
            size="xs"
          />

          <Select
            label="Category"
            value={category}
            onChange={(val) => setCategory(val)}
            data={[
              { value: "Strategy", label: "Strategy" },
              { value: "Lattice CAD", label: "Lattice CAD" },
              { value: "Board", label: "Classic Board" },
              { value: "Ring", label: "Ring Topology" },
              { value: "Custom", label: "Custom Domain" },
            ]}
            size="xs"
          />
        </Group>

        <Stack gap={4}>
          <Text size="xs" fw={500}>
            Vault Visibility & Access Control
          </Text>
          <SegmentedControl
            size="xs"
            value={visibility}
            onChange={(val: any) => setVisibility(val)}
            data={[
              {
                label: (
                  <Group gap={4} justify="center">
                    <RiLockLine size={13} />
                    <span>Private Vault (Only Me)</span>
                  </Group>
                ),
                value: "private",
              },
              {
                label: (
                  <Group gap={4} justify="center">
                    <RiGlobalLine size={13} />
                    <span>Public Gallery (Community)</span>
                  </Group>
                ),
                value: "public",
              },
            ]}
          />
        </Stack>

        <Textarea
          label="Description & Rules Overview"
          placeholder="Brief description of rules, objective, and win conditions..."
          value={description}
          onChange={(e) => setDescription(e.currentTarget.value)}
          size="xs"
          rows={3}
        />

        <Group justify="flex-end" mt="xs">
          <Button variant="subtle" color="gray" size="xs" onClick={onClose}>
            Cancel
          </Button>
          <Button
            color="coral"
            size="xs"
            leftSection={<RiCheckLine size={14} />}
            onClick={handleSave}
            loading={isSaving}
            disabled={!isSafeNameValid}
          >
            Save to Cloud Vault
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default SaveWorldModal;
