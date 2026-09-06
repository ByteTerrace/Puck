import React, { useState } from "react";
import {
  Modal,
  TextInput,
  Select,
  SegmentedControl,
  Button,
  Group,
  Stack,
  Text,
  Paper,
  ActionIcon,
  Divider,
} from "@mantine/core";
import {
  RiAddLine,
  RiDeleteBinLine,
  RiFlashlightLine,
  RiCheckLine,
} from "@remixicon/react";
import { WorldRule } from "../../engine/evaluator";

export interface RuleAuthoringModalProps {
  opened: boolean;
  onClose: () => void;
  onSaveRule: (newRule: WorldRule) => void;
  existingStateVars: string[];
  topologyNames?: string[];
}

export const RuleAuthoringModal: React.FC<RuleAuthoringModalProps> = ({
  opened,
  onClose,
  onSaveRule,
  existingStateVars,
  topologyNames: _topologyNames,
}) => {
  const [ruleName, setRuleName] = useState("");
  const [mode, setMode] = useState<"Edge" | "Level">("Edge");

  // Gate conditions
  const [conditions, setConditions] = useState<
    Array<{ stateVar: string; comparison: string; comparandType: "value" | "state"; value: string }>
  >([
    {
      stateVar: existingStateVars[0] ?? "tttMoveRequest",
      comparison: "NotEqual",
      comparandType: "state",
      value: existingStateVars[1] ?? "tttMoveApplied",
    },
  ]);

  // Effects list
  const [effects, setEffects] = useState<
    Array<{ $type: "setState" | "addState"; state: string; kind: "value" | "expression" | "fromState"; value: string }>
  >([
    {
      $type: "setState",
      state: existingStateVars[0] ?? "tttActive",
      kind: "expression",
      value: "3 - tttActive",
    },
  ]);

  const handleAddCondition = () => {
    setConditions((prev) => [
      ...prev,
      {
        stateVar: existingStateVars[0] ?? "tttWinner",
        comparison: "Equal",
        comparandType: "value",
        value: "0",
      },
    ]);
  };

  const handleRemoveCondition = (index: number) => {
    setConditions((prev) => prev.filter((_, i) => i !== index));
  };

  const handleAddEffect = () => {
    setEffects((prev) => [
      ...prev,
      {
        $type: "setState",
        state: existingStateVars[0] ?? "tttActive",
        kind: "value",
        value: "1",
      },
    ]);
  };

  const handleRemoveEffect = (index: number) => {
    setEffects((prev) => prev.filter((_, i) => i !== index));
  };

  const handleSave = () => {
    const trimmedName = ruleName.trim() || `custom-rule-${Date.now().toString().slice(-4)}`;

    // Build gate
    const predicates = conditions.map((c) => {
      const pred: any = {
        $type: "compareState",
        state: c.stateVar,
        comparison: c.comparison,
      };
      if (c.comparandType === "state") {
        pred.comparandState = c.value;
      } else {
        pred.value = Number(c.value) || c.value;
      }
      return pred;
    });

    const gate: any =
      predicates.length === 1
        ? predicates[0]
        : {
            $type: "all",
            predicates,
          };

    // Build effects
    const builtEffects = effects.map((e) => {
      const eff: any = {
        $type: e.$type,
        state: e.state,
      };
      if (e.kind === "expression") {
        eff.expression = e.value;
      } else if (e.kind === "fromState") {
        eff.fromState = e.value;
      } else {
        eff.value = Number(e.value) || e.value;
      }
      return eff;
    });

    const rule: WorldRule = {
      name: trimmedName,
      mode,
      gate,
      effects: builtEffects,
    };

    onSaveRule(rule);
    onClose();
  };

  const comparisonOptions = [
    { value: "Equal", label: "== (Equal)" },
    { value: "NotEqual", label: "!= (Not Equal)" },
    { value: "Greater", label: "> (Greater Than)" },
    { value: "GreaterOrEqual", label: ">= (Greater/Equal)" },
    { value: "Less", label: "< (Less Than)" },
    { value: "LessOrEqual", label: "<= (Less/Equal)" },
  ];

  return (
    <Modal
      closeButtonProps={{"aria-label":"Close designer"}}
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiFlashlightLine size={18} color="var(--accent)" />
          <Text fw={600} style={{ fontFamily: '"Lora", Georgia, serif' }}>
            Visual Rule & Effect Composer
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
        {/* Rule metadata */}
        <Group grow align="flex-start">
          <TextInput
            label="Rule Identifier"
            placeholder="e.g. ttt-gravity-fall"
            value={ruleName}
            onChange={(e) => setRuleName(e.currentTarget.value)}
            size="xs"
            styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
          />
          <Stack gap={4}>
            <Text size="xs" fw={500}>
              Execution Mode
            </Text>
            <SegmentedControl
              size="xs"
              value={mode}
              onChange={(val: any) => setMode(val)}
              data={[
                { label: "Edge (Event-Driven)", value: "Edge" },
                { label: "Level (Reactive Cascade)", value: "Level" },
              ]}
            />
          </Stack>
        </Group>

        <Divider label="Gating Predicates (Activation Guard)" labelPosition="left" />

        {/* Predicate Builder */}
        <Stack gap="xs">
          {conditions.map((cond, idx) => (
            <Paper key={idx} p="xs" radius="xs" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Group gap="xs" align="center">
                <Select
                  size="xs"
                  style={{ width: 140 }}
                  value={cond.stateVar}
                  onChange={(val) => {
                    if (val) {
                      const updated = [...conditions];
                      updated[idx].stateVar = val;
                      setConditions(updated);
                    }
                  }}
                  data={existingStateVars.map((v) => ({ value: v, label: v }))}
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                <Select
                  size="xs"
                  style={{ width: 130 }}
                  value={cond.comparison}
                  onChange={(val) => {
                    if (val) {
                      const updated = [...conditions];
                      updated[idx].comparison = val;
                      setConditions(updated);
                    }
                  }}
                  data={comparisonOptions}
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                <SegmentedControl
                  size="xs"
                  value={cond.comparandType}
                  onChange={(val: any) => {
                    const updated = [...conditions];
                    updated[idx].comparandType = val;
                    setConditions(updated);
                  }}
                  data={[
                    { label: "Literal", value: "value" },
                    { label: "Register", value: "state" },
                  ]}
                />

                {cond.comparandType === "state" ? (
                  <Select
                    size="xs"
                    style={{ flex: 1 }}
                    value={cond.value}
                    onChange={(val) => {
                      if (val) {
                        const updated = [...conditions];
                        updated[idx].value = val;
                        setConditions(updated);
                      }
                    }}
                    data={existingStateVars.map((v) => ({ value: v, label: v }))}
                    styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                  />
                ) : (
                  <TextInput
                    size="xs"
                    style={{ flex: 1 }}
                    value={cond.value}
                    onChange={(e) => {
                      const updated = [...conditions];
                      updated[idx].value = e.currentTarget.value;
                      setConditions(updated);
                    }}
                    placeholder="e.g. 0"
                    styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                  />
                )}

                {conditions.length > 1 && (
                  <ActionIcon
                    size="sm"
                    color="red"
                    variant="subtle"
                    onClick={() => handleRemoveCondition(idx)}
                  >
                    <RiDeleteBinLine size={14} />
                  </ActionIcon>
                )}
              </Group>
            </Paper>
          ))}

          <Button
            size="compact-xs"
            variant="light"
            color="jade"
            leftSection={<RiAddLine size={12} />}
            onClick={handleAddCondition}
            style={{ alignSelf: "flex-start" }}
          >
            Add Condition
          </Button>
        </Stack>

        <Divider label="Effects Sequence (State Mutations)" labelPosition="left" />

        {/* Effects Builder */}
        <Stack gap="xs">
          {effects.map((eff, idx) => (
            <Paper key={idx} p="xs" radius="xs" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
              <Group gap="xs" align="center">
                <Select
                  size="xs"
                  style={{ width: 110 }}
                  value={eff.$type}
                  onChange={(val: any) => {
                    const updated = [...effects];
                    updated[idx].$type = val;
                    setEffects(updated);
                  }}
                  data={[
                    { value: "setState", label: "Set State" },
                    { value: "addState", label: "Add (+)" },
                  ]}
                />

                <Select
                  size="xs"
                  style={{ width: 140 }}
                  value={eff.state}
                  onChange={(val) => {
                    if (val) {
                      const updated = [...effects];
                      updated[idx].state = val;
                      setEffects(updated);
                    }
                  }}
                  data={existingStateVars.map((v) => ({ value: v, label: v }))}
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                <SegmentedControl
                  size="xs"
                  value={eff.kind}
                  onChange={(val: any) => {
                    const updated = [...effects];
                    updated[idx].kind = val;
                    setEffects(updated);
                  }}
                  data={[
                    { label: "Expr", value: "expression" },
                    { label: "Value", value: "value" },
                    { label: "From", value: "fromState" },
                  ]}
                />

                <TextInput
                  size="xs"
                  style={{ flex: 1 }}
                  value={eff.value}
                  onChange={(e) => {
                    const updated = [...effects];
                    updated[idx].value = e.currentTarget.value;
                    setEffects(updated);
                  }}
                  placeholder="e.g. 3 - tttActive or 1"
                  styles={{ input: { fontFamily: '"JetBrains Mono", monospace' } }}
                />

                {effects.length > 1 && (
                  <ActionIcon
                    size="sm"
                    color="red"
                    variant="subtle"
                    onClick={() => handleRemoveEffect(idx)}
                  >
                    <RiDeleteBinLine size={14} />
                  </ActionIcon>
                )}
              </Group>
            </Paper>
          ))}

          <Button
            size="compact-xs"
            variant="light"
            color="jade"
            leftSection={<RiAddLine size={12} />}
            onClick={handleAddEffect}
            style={{ alignSelf: "flex-start" }}
          >
            Add Mutation Effect
          </Button>
        </Stack>

        {/* Action buttons */}
        <Group justify="flex-end" mt="md">
          <Button variant="subtle" color="gray" size="xs" onClick={onClose}>
            Cancel
          </Button>
          <Button
            color="coral"
            size="xs"
            leftSection={<RiCheckLine size={14} />}
            onClick={handleSave}
          >
            Save Rule to World
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
};

export default RuleAuthoringModal;
