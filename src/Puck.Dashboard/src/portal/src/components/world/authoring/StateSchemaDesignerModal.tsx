import React, { useState, useEffect } from "react";
import {
  Modal,
  TextInput,
  NumberInput,
  Select,
  SegmentedControl,
  Button,
  Group,
  Stack,
  Text,
  Badge,
  Paper,
  ActionIcon,
  Divider,
  SimpleGrid,
  Checkbox,
  ScrollArea,
  Code,
  Tooltip,
} from "@mantine/core";
import {
  RiDatabaseLine,
  RiAddLine,
  RiDeleteBinLine,
  RiCheckLine,
  RiGridFill,
  RiEditLine,
  RiShieldCheckLine,
  RiInformationLine,
} from "@remixicon/react";
import { StateRowDefinition } from "../StateMatrixView";
import { TopologyDefinition } from "../../../engine/evaluator";

export interface StateSchemaDesignerModalProps {
  opened: boolean;
  onClose: () => void;
  stateDefinitions: StateRowDefinition[];
  onSaveStateDefinitions: (newStates: StateRowDefinition[]) => void;
  availableTopologies?: TopologyDefinition[];
}

export const StateSchemaDesignerModal: React.FC<StateSchemaDesignerModalProps> = ({
  opened,
  onClose,
  stateDefinitions,
  onSaveStateDefinitions,
  availableTopologies = [],
}) => {
  const [states, setStates] = useState<StateRowDefinition[]>([]);
  const [selectedIdx, setSelectedIdx] = useState<number | null>(null);

  // Form fields for current / new variable
  const [varCategory, setVarCategory] = useState<"scalar" | "domain">("scalar");
  const [varName, setVarName] = useState<string>("");
  const [varKind, setVarKind] = useState<string>("int");
  const [varValue, setVarValue] = useState<number>(0);
  const [varMin, setVarMin] = useState<number | undefined>(undefined);
  const [varMax, setVarMax] = useState<number | undefined>(undefined);
  const [varNonNegative, setVarNonNegative] = useState<boolean>(false);
  const [varTopology, setVarTopology] = useState<string>(
    availableTopologies[0]?.name ?? ""
  );

  const [formError, setFormError] = useState<string | null>(null);

  // Sync state when modal opens
  useEffect(() => {
    if (opened) {
      setStates(JSON.parse(JSON.stringify(stateDefinitions)));
      setSelectedIdx(null);
      resetForm();
    }
  }, [opened, stateDefinitions]);

  const resetForm = () => {
    setVarCategory("scalar");
    setVarName("");
    setVarKind("int");
    setVarValue(0);
    setVarMin(undefined);
    setVarMax(undefined);
    setVarNonNegative(false);
    setVarTopology(availableTopologies[0]?.name ?? "");
    setFormError(null);
  };

  const handleSelectForEdit = (idx: number) => {
    const item = states[idx];
    if (!item) return;
    if (item.value !== undefined && typeof item.value !== "number") { setFormError("Edit this value in JSON to preserve its authored type."); return; }
    setSelectedIdx(idx);
    setVarName(item.name);
    setVarKind(item.kind ?? "int");
    setVarValue(typeof item.value === "number" ? item.value : 0);
    setVarMin(item.min);
    setVarMax(item.max);
    setVarNonNegative(!!item.nonNegative);

    if (item.domain) {
      setVarCategory("domain");
      setVarTopology(item.domain.topology ?? availableTopologies[0]?.name ?? "");
    } else {
      setVarCategory("scalar");
    }
    setFormError(null);
  };

  const handleValidateAndCommitVariable = () => {
    const trimmedName = varName.trim();
    if (!trimmedName) {
      setFormError("Variable identifier cannot be empty.");
      return;
    }
    if (!/^[a-zA-Z_][a-zA-Z0-9_]*$/.test(trimmedName)) {
      setFormError("Must be a valid identifier (alphanumeric and underscores, starting with letter or _).");
      return;
    }

    // Check duplicates if new or changed name
    const existingIdx = states.findIndex(
      (s, i) => s.name === trimmedName && i !== selectedIdx
    );
    if (existingIdx !== -1) {
      setFormError(`Variable '${trimmedName}' already exists in state schema.`);
      return;
    }

    const newDef: StateRowDefinition = {
      ...(selectedIdx !== null ? states[selectedIdx] : {}),
      name: trimmedName,
      kind: varKind,
    };

    if (varCategory === "domain") {
      delete newDef.value;
      newDef.domain = {
        ...(selectedIdx !== null ? states[selectedIdx].domain : {}),
        $type: "cellsOf",
        topology: varTopology || (availableTopologies[0]?.name ?? "board"),
      };
    } else {
      delete newDef.domain;
      delete (newDef as any).cells;
      delete newDef.min; delete newDef.max; delete newDef.nonNegative;
      newDef.value = varValue;
      if (varMin !== undefined) newDef.min = varMin;
      if (varMax !== undefined) newDef.max = varMax;
      if (varNonNegative) newDef.nonNegative = true;
    }

    if (selectedIdx !== null && selectedIdx >= 0 && selectedIdx < states.length) {
      setStates((prev) => {
        const next = [...prev];
        next[selectedIdx] = newDef;
        return next;
      });
      setSelectedIdx(null);
    } else {
      setStates((prev) => [...prev, newDef]);
    }

    resetForm();
  };

  const handleDelete = (idx: number) => {
    setStates((prev) => prev.filter((_, i) => i !== idx));
    if (selectedIdx === idx) {
      setSelectedIdx(null);
      resetForm();
    }
  };

  const handleSaveAll = () => {
    onSaveStateDefinitions(states);
    onClose();
  };

  return (
    <Modal
      closeButtonProps={{"aria-label":"Close designer"}}
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiDatabaseLine size={20} color="var(--accent-2)" />
          <Text fw={700} style={{ fontFamily: "Outfit, sans-serif" }}>
            State Schema & Board Variable Designer
          </Text>
        </Group>
      }
      size="xl"
      styles={{
        content: {
          background: "var(--paper-2)",
          border: "1px solid var(--border)",
        },
      }}
    >
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          Define world variables, scalar registries, and spatial cell-board domains bound to your topologies.
        </Text>

        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="md">
          {/* Left Column: Schema Variable List */}
          <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
            <Group justify="space-between" mb="xs">
              <Text size="sm" fw={600} c="dimmed">
                WORLD SCHEMA VARIABLES ({states.length})
              </Text>
              <Button
                size="compact-xs"
                variant="light"
                color="teal"
                leftSection={<RiAddLine size={14} />}
                onClick={() => {
                  setSelectedIdx(null);
                  resetForm();
                }}
              >
                New Variable
              </Button>
            </Group>

            <ScrollArea h={320} offsetScrollbars>
              <Stack gap="xs">
                {states.length === 0 ? (
                  <Text size="xs" c="dimmed" fs="italic" ta="center" py="xl">
                    No state variables declared. Add one below.
                  </Text>
                ) : (
                  states.map((st, idx) => {
                    const isSelected = selectedIdx === idx;
                    const isDomain = !!st.domain;
                    return (
                      <Paper
                        key={st.name + idx}
                        p="xs"
                        withBorder
                        style={{
                          cursor: "pointer",
                          borderColor: isSelected ? "var(--accent)" : "var(--border)",
                          background: isSelected ? "rgba(242, 135, 154, 0.08)" : "var(--paper-2)",
                        }}
                        onClick={() => handleSelectForEdit(idx)}
                      >
                        <Group justify="space-between" wrap="nowrap">
                          <Group gap="xs" wrap="nowrap">
                            {isDomain ? (
                              <RiGridFill size={16} color="var(--accent-2)" />
                            ) : (
                              <RiDatabaseLine size={16} color="var(--accent)" />
                            )}
                            <div>
                              <Text size="xs" fw={700} style={{ fontFamily: "monospace" }}>
                                {st.name}
                              </Text>
                              <Text size="10px" c="dimmed">
                                {isDomain
                                  ? `cellsOf(${st.domain?.topology})`
                                  : `kind: ${st.kind ?? "int"} | val: ${st.value ?? 0}`}
                              </Text>
                            </div>
                          </Group>
                          <Group gap={4}>
                            <Badge
                              size="xs"
                              variant="light"
                              color={isDomain ? "cyan" : "teal"}
                            >
                              {isDomain ? "Domain" : "Scalar"}
                            </Badge>
                            <Tooltip label="Edit Variable">
                              <ActionIcon
                                size="xs"
                                variant="subtle"
                                color="blue"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  handleSelectForEdit(idx);
                                }}
                              >
                                <RiEditLine size={12} />
                              </ActionIcon>
                            </Tooltip>
                            <Tooltip label="Delete Variable">
                              <ActionIcon
                                size="xs"
                                variant="subtle"
                                color="red"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  handleDelete(idx);
                                }}
                              >
                                <RiDeleteBinLine size={12} />
                              </ActionIcon>
                            </Tooltip>
                          </Group>
                        </Group>
                      </Paper>
                    );
                  })
                )}
              </Stack>
            </ScrollArea>
          </Paper>

          {/* Right Column: Variable Editor Form */}
          <Paper p="sm" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
            <Group justify="space-between" mb="xs">
              <Text size="sm" fw={600} c="dimmed">
                {selectedIdx !== null ? `EDIT: ${states[selectedIdx]?.name}` : "ADD NEW VARIABLE"}
              </Text>
              {selectedIdx !== null && (
                <Badge size="xs" color="yellow" variant="light">
                  Editing #{selectedIdx + 1}
                </Badge>
              )}
            </Group>

            <Stack gap="xs">
              <SegmentedControl
                size="xs"
                value={varCategory}
                onChange={(v) => setVarCategory(v as "scalar" | "domain")}
                data={[
                  { label: "Scalar Variable", value: "scalar" },
                  { label: "Cell-Board Domain", value: "domain" },
                ]}
              />

              <TextInput
                label="Variable Name"
                placeholder="e.g. p1Score, tttMoveRequest, boardState"
                size="xs"
                value={varName}
                onChange={(e) => setVarName(e.currentTarget.value)}
                error={formError}
                required
              />

              <Group grow>
                <Select
                  label="Kind / Type"
                  size="xs"
                  value={varKind}
                  onChange={(v) => setVarKind(v || "int")}
                  data={[
                    { value: "int", label: "int (64-bit integer)" },
                    { value: "bool", label: "bool (boolean flag)" },
                    { value: "string", label: "string (text value)" },
                  ]}
                />
                {varCategory === "scalar" && (
                  <NumberInput
                    label="Default Value"
                    size="xs"
                    value={varValue}
                    onChange={(v) => setVarValue(Number(v) || 0)}
                  />
                )}
              </Group>

              {varCategory === "domain" ? (
                <Stack gap="xs" mt="xs">
                  <Select
                    label="Bound Topology (Spatial Lattice)"
                    size="xs"
                    value={varTopology}
                    onChange={(v) => setVarTopology(v || "")}
                    data={
                      availableTopologies.length > 0
                        ? availableTopologies.map((t) => ({ value: t.name, label: `${t.name} (${t.$type})` }))
                        : [{ value: "default", label: "No topologies available" }]
                    }
                    description="Cells of this topology will serve as the addressable domain keys."
                  />
                  <Paper p="xs" style={{ background: "rgba(79, 208, 180, 0.08)", border: "1px dashed var(--accent-2)" }}>
                    <Group gap="xs">
                      <RiInformationLine size={16} color="var(--accent-2)" />
                      <Text size="xs" c="dimmed">
                        State reads and writes can access cells using key addresses like <code>$cell:moveIdx:$value</code>.
                      </Text>
                    </Group>
                  </Paper>
                </Stack>
              ) : (
                <Stack gap="xs">
                  <Group grow>
                    <NumberInput
                      label="Min Constraint (Optional)"
                      size="xs"
                      placeholder="e.g. 0"
                      value={varMin ?? ""}
                      onChange={(v) => setVarMin(v === "" ? undefined : Number(v))}
                    />
                    <NumberInput
                      label="Max Constraint (Optional)"
                      size="xs"
                      placeholder="e.g. 64"
                      value={varMax ?? ""}
                      onChange={(v) => setVarMax(v === "" ? undefined : Number(v))}
                    />
                  </Group>
                  <Checkbox
                    size="xs"
                    label="Enforce Non-Negative (value >= 0)"
                    checked={varNonNegative}
                    onChange={(e) => setVarNonNegative(e.currentTarget.checked)}
                  />
                </Stack>
              )}

              <Button
                mt="xs"
                size="xs"
                variant="gradient"
                gradient={{ from: "teal", to: "cyan" }}
                leftSection={selectedIdx !== null ? <RiCheckLine size={14} /> : <RiAddLine size={14} />}
                onClick={handleValidateAndCommitVariable}
              >
                {selectedIdx !== null ? "Update Variable" : "Add Variable to Schema"}
              </Button>
            </Stack>
          </Paper>
        </SimpleGrid>

        <Divider my="xs" />

        {/* Live Schema Preview */}
        <Paper p="xs" withBorder style={{ background: "var(--paper)", borderColor: "var(--border)" }}>
          <Text size="11px" fw={600} c="dimmed" mb={4}>
            SCHEMA PREVIEW JSON (STATE SLICE):
          </Text>
          <Code block style={{ maxHeight: 110, overflow: "auto", fontSize: 10 }}>
            {JSON.stringify(states, null, 2)}
          </Code>
        </Paper>

        <Group justify="flex-end" mt="xs">
          <Button variant="subtle" color="gray" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="filled"
            color="pink"
            leftSection={<RiShieldCheckLine size={16} />}
            onClick={handleSaveAll}
          >
            Apply Schema to World ({states.length} variables)
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default StateSchemaDesignerModal;
