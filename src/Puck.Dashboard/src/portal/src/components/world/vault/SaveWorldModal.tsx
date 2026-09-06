import React, { useState, useEffect } from "react";
import {
  Modal,
  TextInput,
  Textarea,
  Select,
  Button,
  Group,
  Stack,
  Text,
  Alert,
} from "@mantine/core";
import {
  RiCloudLine,
  RiCheckLine,
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
  useEffect(() => { if (opened) { setWorldId(currentId); setName(defaultName); setError(null); } }, [opened,currentId,defaultName]);
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
        visibility: "private",
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
      closeButtonProps={{"aria-label":"Close designer"}}
      opened={opened}
      onClose={() => { if (!isSaving) onClose(); }}
      title={
        <Group gap="xs">
          <RiCloudLine size={18} color="var(--accent)" />
          <Text fw={600} style={{ fontFamily: '"Lora", Georgia, serif' }}>
            Save document locally
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
          label="Document ID"
          description="A short name for this document in your local library."
          placeholder="e.g. hypercube-tactics"
          value={worldId}
          onChange={(e) => setWorldId(e.currentTarget.value)}
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

        <Text size="sm" c="var(--ink-soft)">Saved in this browser. Export JSON for a portable backup.</Text>

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
            Save locally
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default SaveWorldModal;
