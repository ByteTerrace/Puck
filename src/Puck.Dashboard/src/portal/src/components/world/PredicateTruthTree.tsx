import React, { useState } from "react";
import { Box, Group, Text, Badge, Paper } from "@mantine/core";
import {
  RiCheckLine,
  RiCloseLine,
  RiArrowRightSLine,
  RiArrowDownSLine,
  RiFilterLine,
} from "@remixicon/react";
import {
  PuckPredicate,
  evaluatePuckPredicate,
  resolveEffectKey,
} from "../../engine/evaluator";

export interface PredicateTruthTreeProps {
  gate: PuckPredicate | undefined;
  state: Record<string, any>;
  boardCells: Record<string, Record<number, number>>;
  initialExpanded?: boolean;
}

interface EvaluatedLeaf {
  passed: boolean;
  leftLabel: string;
  leftValue: any;
  operator: string;
  rightLabel: string;
  rightValue: any;
}

function evaluateLeafPredicate(
  pred: PuckPredicate,
  state: Record<string, any>,
  boardCells: Record<string, Record<number, number>>
): EvaluatedLeaf {
  let leftVal: any = 0;
  let leftLabel = pred.state ?? "";

  if (pred.key) {
    const resolvedKey = resolveEffectKey(pred.key, state);
    leftLabel = `${pred.state}[${resolvedKey ?? pred.key}]`;
    if (resolvedKey !== null && typeof resolvedKey === "number") {
      leftVal = boardCells[pred.state ?? ""]?.[resolvedKey] ?? 0;
    }
  } else {
    leftVal = state[pred.state ?? ""] ?? 0;
  }

  let rightVal: any = pred.value ?? 0;
  let rightLabel = String(pred.value ?? "0");
  if (pred.comparandState) {
    rightLabel = pred.comparandState;
    rightVal = state[pred.comparandState] ?? 0;
  }

  const evalResult = evaluatePuckPredicate(pred, state, boardCells);

  return {
    passed: evalResult.passed,
    leftLabel,
    leftValue: leftVal,
    operator: pred.comparison ?? "Equal",
    rightLabel,
    rightValue: rightVal,
  };
}

export const PredicateTruthNode: React.FC<{
  pred: PuckPredicate;
  state: Record<string, any>;
  boardCells: Record<string, Record<number, number>>;
  depth?: number;
}> = ({ pred, state, boardCells, depth = 0 }) => {
  const [expanded, setExpanded] = useState(true);

  if (!pred) return null;

  // Composite node: ALL
  if (pred.$type === "all") {
    const children: PuckPredicate[] = pred.predicates ?? [];
    const nodeEval = evaluatePuckPredicate(pred, state, boardCells);
    const passed = nodeEval.passed;

    return (
      <Box style={{ marginLeft: depth * 14, marginTop: 4, marginBottom: 4 }}>
        <Group
          gap={6}
          onClick={() => setExpanded(!expanded)}
          style={{ cursor: "pointer", userSelect: "none" }}
        >
          {expanded ? (
            <RiArrowDownSLine size={14} color="var(--ink-faint)" />
          ) : (
            <RiArrowRightSLine size={14} color="var(--ink-faint)" />
          )}
          <Badge
            size="xs"
            variant="light"
            color={passed ? "teal" : "red"}
            style={{ fontFamily: '"JetBrains Mono", monospace' }}
          >
            AND ({passed ? "ALL PASSED" : "FAILED"})
          </Badge>
          <Text size="xs" style={{ color: "var(--ink-faint)", fontFamily: '"JetBrains Mono", monospace' }}>
            {children.length} condition{children.length === 1 ? "" : "s"}
          </Text>
        </Group>

        {expanded && (
          <Box
            style={{
              borderLeft: "1px dashed var(--rule)",
              marginLeft: 6,
              paddingLeft: 8,
              marginTop: 4,
            }}
          >
            {children.map((child: PuckPredicate, idx: number) => (
              <PredicateTruthNode
                key={idx}
                pred={child}
                state={state}
                boardCells={boardCells}
                depth={depth + 1}
              />
            ))}
          </Box>
        )}
      </Box>
    );
  }

  // Composite node: ANY
  if (pred.$type === "any") {
    const children: PuckPredicate[] = pred.predicates ?? [];
    const nodeEval = evaluatePuckPredicate(pred, state, boardCells);
    const passed = nodeEval.passed;

    return (
      <Box style={{ marginLeft: depth * 14, marginTop: 4, marginBottom: 4 }}>
        <Group
          gap={6}
          onClick={() => setExpanded(!expanded)}
          style={{ cursor: "pointer", userSelect: "none" }}
        >
          {expanded ? (
            <RiArrowDownSLine size={14} color="var(--ink-faint)" />
          ) : (
            <RiArrowRightSLine size={14} color="var(--ink-faint)" />
          )}
          <Badge
            size="xs"
            variant="light"
            color={passed ? "teal" : "red"}
            style={{ fontFamily: '"JetBrains Mono", monospace' }}
          >
            OR ({passed ? "AT LEAST 1 PASSED" : "ALL FAILED"})
          </Badge>
          <Text size="xs" style={{ color: "var(--ink-faint)", fontFamily: '"JetBrains Mono", monospace' }}>
            {children.length} branch{children.length === 1 ? "" : "es"}
          </Text>
        </Group>

        {expanded && (
          <Box
            style={{
              borderLeft: "1px dashed var(--rule)",
              marginLeft: 6,
              paddingLeft: 8,
              marginTop: 4,
            }}
          >
            {children.map((child: PuckPredicate, idx: number) => (
              <PredicateTruthNode
                key={idx}
                pred={child}
                state={state}
                boardCells={boardCells}
                depth={depth + 1}
              />
            ))}
          </Box>
        )}
      </Box>
    );
  }

  // Composite node: NOT
  if (pred.$type === "not") {
    const nodeEval = evaluatePuckPredicate(pred, state, boardCells);
    const passed = nodeEval.passed;

    return (
      <Box style={{ marginLeft: depth * 14, marginTop: 4, marginBottom: 4 }}>
        <Badge
          size="xs"
          variant="light"
          color={passed ? "teal" : "red"}
          style={{ fontFamily: '"JetBrains Mono", monospace' }}
        >
          NOT ({passed ? "INVERT PASSED" : "FAILED"})
        </Badge>
        {pred.predicate && (
          <PredicateTruthNode
            pred={pred.predicate}
            state={state}
            boardCells={boardCells}
            depth={depth + 1}
          />
        )}
      </Box>
    );
  }

  // Leaf node: compareState
  const leaf = evaluateLeafPredicate(pred, state, boardCells);

  const opSymbols: Record<string, string> = {
    Equal: "==",
    NotEqual: "!=",
    Greater: ">",
    GreaterOrEqual: ">=",
    Less: "<",
    LessOrEqual: "<=",
  };
  const opSym = opSymbols[leaf.operator] ?? leaf.operator;

  return (
    <Paper
      withBorder
      p={4}
      mb={3}
      style={{
        marginLeft: depth * 8,
        background: leaf.passed ? "rgba(79, 208, 180, 0.05)" : "rgba(242, 135, 154, 0.08)",
        borderColor: leaf.passed ? "rgba(79, 208, 180, 0.25)" : "rgba(242, 135, 154, 0.35)",
        borderRadius: 4,
      }}
    >
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Group gap={6} wrap="nowrap">
          {leaf.passed ? (
            <RiCheckLine size={13} color="var(--accent-2)" />
          ) : (
            <RiCloseLine size={13} color="var(--accent)" />
          )}

          <Text
            size="xs"
            style={{
              fontFamily: '"JetBrains Mono", monospace',
              fontSize: 11,
              color: leaf.passed ? "var(--ink)" : "var(--ink-soft)",
            }}
          >
            <span style={{ color: "var(--ink-soft)" }}>{leaf.leftLabel}</span>
            <span style={{ color: "var(--ink-faint)" }}> (</span>
            <span style={{ fontWeight: 600 }}>{String(leaf.leftValue)}</span>
            <span style={{ color: "var(--ink-faint)" }}>)</span>{" "}
            <span style={{ color: "var(--accent-2)", fontWeight: 600 }}>{opSym}</span>{" "}
            <span style={{ color: "var(--ink-soft)" }}>{leaf.rightLabel}</span>
            {leaf.rightLabel !== String(leaf.rightValue) && (
              <>
                <span style={{ color: "var(--ink-faint)" }}> (</span>
                <span style={{ fontWeight: 600 }}>{String(leaf.rightValue)}</span>
                <span style={{ color: "var(--ink-faint)" }}>)</span>
              </>
            )}
          </Text>
        </Group>

        <Badge
          size="xs"
          variant="filled"
          color={leaf.passed ? "teal" : "red"}
          style={{ fontSize: 9, height: 16, padding: "0 6px" }}
        >
          {leaf.passed ? "TRUE" : "BLOCK"}
        </Badge>
      </Group>
    </Paper>
  );
};

export const PredicateTruthTree: React.FC<PredicateTruthTreeProps> = ({
  gate,
  state,
  boardCells,
  initialExpanded = false,
}) => {
  const [expanded, setExpanded] = useState(initialExpanded);

  if (!gate) {
    return (
      <Text size="xs" c="dimmed" fs="italic">
        No gating predicate (unconditional rule)
      </Text>
    );
  }

  const overall = evaluatePuckPredicate(gate, state, boardCells);

  return (
    <Box>
      <Group
        gap="xs"
        onClick={() => setExpanded(!expanded)}
        style={{ cursor: "pointer", userSelect: "none" }}
      >
        <RiFilterLine size={13} color={overall.passed ? "var(--accent-2)" : "var(--accent)"} />
        <Text size="xs" fw={600} style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-soft)" }}>
          Gate Truth Tree
        </Text>
        <Badge size="xs" variant="light" color={overall.passed ? "teal" : "red"}>
          {overall.passed ? "PASS" : "BLOCKED"}
        </Badge>
        {expanded ? (
          <RiArrowDownSLine size={13} color="var(--ink-faint)" />
        ) : (
          <RiArrowRightSLine size={13} color="var(--ink-faint)" />
        )}
      </Group>

      {expanded && (
        <Box mt={6}>
          <PredicateTruthNode pred={gate} state={state} boardCells={boardCells} />
        </Box>
      )}
    </Box>
  );
};

export default PredicateTruthTree;
