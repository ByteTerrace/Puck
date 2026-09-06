import React from "react";
import {
  Card,
  Group,
  Stack,
  Text,
  Badge,
  ScrollArea,
  Paper,
} from "@mantine/core";
import {
  RiGitCommitLine,
  RiCheckFill,
  RiCloseFill,
  RiTimeLine,
} from "@remixicon/react";
import { TickSnapshot } from "../../engine/replayTape";

export interface ExecutionTraceLogProps {
  snapshots: TickSnapshot[];
  currentTickIndex: number;
  onJumpToTick: (tickIndex: number) => void;
}

export const ExecutionTraceLog: React.FC<ExecutionTraceLogProps> = ({
  snapshots,
  currentTickIndex,
  onJumpToTick,
}) => {
  return (
    <Card withBorder radius="md" p="sm" style={{ background: "var(--paper-2)", borderColor: "var(--rule)" }}>
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiGitCommitLine size={18} color="var(--accent)" />
          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
            Causal Execution Trace Log
          </Text>
          <Badge variant="light" color="coral" size="xs">
            {snapshots.length} Ticks
          </Badge>
        </Group>

        <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-faint)" }}>
          Click tick to time-travel
        </Text>
      </Group>

      <ScrollArea h={420} offsetScrollbars>
        <Stack gap="xs">
          {snapshots.map((snap, idx) => {
            const isCurrent = idx === currentTickIndex;
            const trace = snap.trace;
            const ruleEvents = trace?.ruleEvents ?? [];
            const firedEvents = ruleEvents.filter((e) => e.fired);
            const blockedEvents = ruleEvents.filter((e) => !e.fired);

            return (
              <Paper
                key={`snap-${idx}`}
                p="xs"
                radius="sm"
                onClick={() => onJumpToTick(idx)}
                style={{
                  cursor: "pointer",
                  background: isCurrent ? "var(--quote-bg)" : "var(--paper-2)",
                  border: isCurrent ? "2px solid var(--accent)" : "1px solid var(--rule)",
                  transition: "all 0.15s ease",
                }}
              >
                <Group justify="space-between" mb={4}>
                  <Group gap={6}>
                    <Badge
                      size="xs"
                      color={isCurrent ? "coral" : "gray"}
                      variant={isCurrent ? "filled" : "light"}
                    >
                      Tick #{snap.tickNumber}
                    </Badge>
                    <Text fw={600} size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink)" }}>
                      {trace?.intentDescription || "Tick"}
                    </Text>
                  </Group>

                  {isCurrent && (
                    <Badge size="xs" color="jade" variant="light" leftSection={<RiTimeLine size={10} />}>
                      Current Active State
                    </Badge>
                  )}
                </Group>

                {/* State deltas mutated in this tick */}
                {trace?.allDeltas && trace.allDeltas.length > 0 && (
                  <Group gap={4} mb={6} wrap="wrap">
                    <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink-faint)", fontSize: 10 }}>
                      MUTATIONS:
                    </Text>
                    {trace.allDeltas.map((d, dIdx) => (
                      <Badge
                        key={dIdx}
                        size="xs"
                        variant="outline"
                        color="coral"
                        style={{ fontFamily: '"JetBrains Mono", monospace', fontSize: 10 }}
                      >
                        {d.target}{d.key !== undefined ? `[${d.key}]` : ""}: {String(d.oldValue)} ➔ {String(d.newValue)}
                      </Badge>
                    ))}
                  </Group>
                )}

                {/* Fired rules */}
                {firedEvents.length > 0 && (
                  <Stack gap={4} mt={4}>
                    {firedEvents.map((e, eIdx) => (
                      <Paper
                        key={eIdx}
                        p={4}
                        radius="xs"
                        style={{
                          background: "var(--code-bg)",
                          borderLeft: e.mode === "Edge" ? "3px solid var(--accent)" : "3px solid var(--accent-2)",
                        }}
                      >
                        <Group justify="space-between" wrap="nowrap">
                          <Group gap={6} wrap="nowrap">
                            <RiCheckFill size={12} color={e.mode === "Edge" ? "var(--accent)" : "var(--accent-2)"} />
                            <Badge size="xs" variant="light" color={e.mode === "Edge" ? "coral" : "jade"}>
                              {e.mode}
                            </Badge>
                            <Text size="xs" ff="monospace" style={{ color: "var(--code-fg)", fontWeight: 600 }}>
                              {e.ruleName}
                            </Text>
                          </Group>
                          <Text size="xs" c="var(--ink-faint)" ff="monospace" style={{ fontSize: 10 }}>
                            {e.gateSummary}
                          </Text>
                        </Group>
                      </Paper>
                    ))}
                  </Stack>
                )}

                {/* Blocked rules if intent was rejected */}
                {blockedEvents.length > 0 && firedEvents.length === 0 && (
                  <Stack gap={4} mt={4}>
                    {blockedEvents.slice(0, 2).map((e, eIdx) => (
                      <Paper
                        key={eIdx}
                        p={4}
                        radius="xs"
                        style={{
                          background: "var(--code-bg)",
                          borderLeft: "3px solid var(--ink-faint)",
                        }}
                      >
                        <Group justify="space-between" wrap="nowrap">
                          <Group gap={6} wrap="nowrap">
                            <RiCloseFill size={12} color="#847d93" />
                            <Text size="xs" ff="monospace" style={{ color: "#847d93" }}>
                              {e.ruleName}: BLOCKED
                            </Text>
                          </Group>
                          <Text size="xs" c="var(--ink-faint)" ff="monospace" style={{ fontSize: 10 }}>
                            {e.gateSummary}
                          </Text>
                        </Group>
                      </Paper>
                    ))}
                  </Stack>
                )}
              </Paper>
            );
          })}
        </Stack>
      </ScrollArea>
    </Card>
  );
};

export default ExecutionTraceLog;
