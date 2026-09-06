import React, { useState, useRef, useEffect } from "react";
import {
  Card,
  Group,
  Stack,
  Text,
  Badge,
  TextInput,
  ActionIcon,
  ScrollArea,
  Paper,
  Tooltip,
  Box,
} from "@mantine/core";
import {
  RiTerminalBoxLine,
  RiSendPlaneLine,
  RiDeleteBinLine,
  RiInformationLine,
} from "@remixicon/react";
import {
  evaluatePuckExpression,
  TopologyDefinition,
} from "../../engine/evaluator";

export interface PuckReplConsoleProps {
  state: Record<string, any>;
  boardCells: Record<string, Record<number, number>>;
  topologies: Record<string, TopologyDefinition>;
}

interface ReplEntry {
  input: string;
  output: any;
  type: string;
  isError?: boolean;
}

export const PuckReplConsole: React.FC<PuckReplConsoleProps> = ({
  state,
  boardCells,
  topologies,
}) => {
  const [input, setInput] = useState("");
  const [history, setHistory] = useState<ReplEntry[]>([
    {
      input: "tttActive",
      output: state["tttActive"] ?? 1,
      type: "number",
    },
    {
      input: "$board:mask:tttBoard:1:1",
      output: state["tttMaskX"] ?? 0n,
      type: "bigint",
    },
  ]);
  const [historyIndex, setHistoryIndex] = useState<number>(-1);
  const scrollViewportRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (scrollViewportRef.current) {
      scrollViewportRef.current.scrollTo({
        top: scrollViewportRef.current.scrollHeight,
        behavior: "smooth",
      });
    }
  }, [history]);

  const handleEvaluate = () => {
    const trimmed = input.trim();
    if (!trimmed) return;

    try {
      const res = evaluatePuckExpression(trimmed, state, boardCells, topologies);
      const entryType = typeof res;
      setHistory((prev) => [
        ...prev,
        { input: trimmed, output: res, type: entryType },
      ]);
    } catch (err: any) {
      setHistory((prev) => [
        ...prev,
        { input: trimmed, output: String(err?.message ?? err), type: "error", isError: true },
      ]);
    }

    setInput("");
    setHistoryIndex(-1);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") {
      e.preventDefault();
      handleEvaluate();
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      if (history.length > 0) {
        const nextIdx = historyIndex === -1 ? history.length - 1 : Math.max(0, historyIndex - 1);
        setHistoryIndex(nextIdx);
        setInput(history[nextIdx]?.input ?? "");
      }
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      if (historyIndex !== -1) {
        const nextIdx = historyIndex + 1;
        if (nextIdx >= history.length) {
          setHistoryIndex(-1);
          setInput("");
        } else {
          setHistoryIndex(nextIdx);
          setInput(history[nextIdx]?.input ?? "");
        }
      }
    }
  };

  const formatOutput = (val: any, type: string) => {
    if (type === "bigint") {
      const bVal = BigInt(val);
      const hex = "0x" + bVal.toString(16).toUpperCase().padStart(16, "0");
      const bitCount = bVal.toString(2).split("1").length - 1;
      return `${bVal.toString()}n (${hex} | ${bitCount} bits set)`;
    }
    if (typeof val === "object") {
      return JSON.stringify(val);
    }
    return String(val);
  };

  const quickSamples = [
    { label: "Active Player", expr: "tttActive" },
    { label: "Winner", expr: "tttWinner" },
    { label: "P1 Mask", expr: "$board:mask:tttBoard:1:1" },
    { label: "L8 Shift P1", expr: "boardShift(tttMaskX, tttCube, L8)" },
    { label: "P1 Win Flag", expr: "tttXWin" },
  ];

  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiTerminalBoxLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Puck Expression REPL
          </Text>
          <Badge variant="light" color="jade" size="xs" style={{ fontFamily: '"JetBrains Mono", monospace' }}>
            LIVE CONTEXT
          </Badge>
        </Group>

        <Group gap="xs">
          <Tooltip label="Clear REPL history">
            <ActionIcon
              size="sm"
              variant="subtle"
              color="gray"
              onClick={() => setHistory([])}
            >
              <RiDeleteBinLine size={14} />
            </ActionIcon>
          </Tooltip>
        </Group>
      </Group>

      {/* Quick Snippet Chips */}
      <Group gap={4} mb="xs" wrap="wrap">
        <RiInformationLine size={12} color="var(--ink-faint)" />
        <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: 10, color: "var(--ink-faint)" }}>
          Quick Queries:
        </Text>
        {quickSamples.map((s, idx) => (
          <Badge
            key={idx}
            size="xs"
            variant="outline"
            color="jade"
            style={{ cursor: "pointer", fontFamily: '"JetBrains Mono", monospace', fontSize: 10 }}
            onClick={() => {
              setInput(s.expr);
            }}
          >
            {s.label}
          </Badge>
        ))}
      </Group>

      {/* REPL Terminal Output Window */}
      <Paper
        p="xs"
        radius="sm"
        style={{
          background: "var(--code-bg)",
          border: "1px solid var(--code-border)",
          minHeight: 180,
          maxHeight: 240,
        }}
      >
        <ScrollArea h={160} viewportRef={scrollViewportRef} offsetScrollbars>
          <Stack gap={6}>
            {history.map((h, idx) => (
              <Box key={idx}>
                {/* Input line */}
                <Group gap={6} align="flex-start" wrap="nowrap">
                  <Text
                    size="xs"
                    style={{
                      fontFamily: '"JetBrains Mono", monospace',
                      color: "var(--accent-2)",
                      fontWeight: 700,
                    }}
                  >
                    puck&gt;
                  </Text>
                  <Text
                    size="xs"
                    style={{
                      fontFamily: '"JetBrains Mono", monospace',
                      color: "var(--code-fg)",
                      wordBreak: "break-all",
                    }}
                  >
                    {h.input}
                  </Text>
                </Group>

                {/* Evaluated Output line */}
                <Group gap={6} align="flex-start" wrap="nowrap" ml={18}>
                  <Text
                    size="xs"
                    style={{
                      fontFamily: '"JetBrains Mono", monospace',
                      color: h.isError ? "var(--accent)" : "var(--accent)",
                      fontWeight: 600,
                    }}
                  >
                    &#x21D2;
                  </Text>
                  <Text
                    size="xs"
                    style={{
                      fontFamily: '"JetBrains Mono", monospace',
                      color: h.isError
                        ? "var(--accent)"
                        : h.type === "bigint"
                        ? "var(--accent-2)"
                        : "var(--ink-soft)",
                      wordBreak: "break-all",
                    }}
                  >
                    {formatOutput(h.output, h.type)}
                  </Text>
                </Group>
              </Box>
            ))}
          </Stack>
        </ScrollArea>
      </Paper>

      {/* Input Prompt Box */}
      <Group gap="xs" mt="xs">
        <TextInput
          flex={1}
          size="xs"
          placeholder="Evaluate expression (e.g. boardShift(tttMaskX, tttCube, L0) != 0n)"
          value={input}
          onChange={(e) => setInput(e.currentTarget.value)}
          onKeyDown={handleKeyDown}
          styles={{
            input: {
              fontFamily: '"JetBrains Mono", monospace',
              background: "var(--code-bg)",
              borderColor: "var(--code-border)",
              color: "var(--code-fg)",
            },
          }}
        />
        <ActionIcon
          size="sm"
          color="coral"
          variant="filled"
          onClick={handleEvaluate}
          disabled={!input.trim()}
        >
          <RiSendPlaneLine size={14} />
        </ActionIcon>
      </Group>
    </Card>
  );
};

export default PuckReplConsole;
