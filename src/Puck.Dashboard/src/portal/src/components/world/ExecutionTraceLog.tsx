import React, { useState } from "react";
import { Button, Card, Group, Pagination, Stack, Text } from "@mantine/core";
import { TickSnapshot } from "../../engine/replayTape";
import PredicateTruthTree from "./PredicateTruthTree";
export interface ExecutionTraceLogProps {
  snapshots: TickSnapshot[];
  currentTickIndex: number;
  onJumpToTick: (index: number) => void;
}
export const ExecutionTraceLog: React.FC<ExecutionTraceLogProps> = ({ snapshots, currentTickIndex, onJumpToTick }) => {
  const [page, setPage] = useState(1);
  const [expanded, setExpanded] = useState<number | null>(null);
  const count = Math.max(1, Math.ceil(snapshots.length / 10));
  const currentPage = Math.min(page, count);
  return <Card
    withBorder
    radius="md"
    p="md"><Stack><Text
      component="h2"
      size="md"
      fw={650}>Preview history</Text>
      <Text
        size="sm"
        c="var(--ink-soft)">Up to 128 snapshots. Board moves include an input tick and an idle tick. Expand a snapshot to inspect gates at the time they ran.</Text>
      {snapshots.slice((currentPage - 1) * 10, currentPage * 10).map((snap, position) => {
        const index = (currentPage - 1) * 10 + position;
        return <div
          key={snap.tickNumber}
          className="studio-trace-row">
          <Group
            justify="space-between"><Text
              size="sm"
              fw={600}>Tick {snap.tickNumber}{index === currentTickIndex ? " · current" : ""}</Text><Group
                gap="xs"><Button
                  variant="default"
                  disabled={index === currentTickIndex}
                  onClick={() => onJumpToTick(index)}>Go to tick {snap.tickNumber}</Button><Button
                    variant="subtle"
                    aria-expanded={expanded === index}
                    onClick={() => setExpanded(expanded === index ? null : index)}>Details</Button></Group></Group>
          <Text
            size="xs"
            c="var(--ink-soft)">{snap.trace?.intentDescription}</Text>
          {expanded === index && <Stack
            gap="sm"
            mt="sm">{snap.trace?.ruleEvents.map((event, i) => <div
              key={i}><Text
                size="sm"
                fw={600}>{event.ruleName} · {event.mode} · {event.fired ? "fired" : "did not fire"}</Text><Text
                  size="xs"
                  c="var(--ink-soft)">{event.gateSummary}</Text>
              {event.deltas.map((delta, j) => <Text
                size="xs"
                ff="monospace"
                key={j}>{delta.target}{delta.key !== undefined ? "[" + delta.key + "]" : ""}: {String(delta.oldValue)} → {String(delta.newValue)}</Text>)}
              {event.gate && <PredicateTruthTree
                gate={event.gate}
                state={event.gateState ?? snap.state}
                boardCells={event.gateBoardCells ?? snap.boardCells}
                initialExpanded={false} />}
            </div>)}</Stack>}
        </div>;
      })}
      {count > 1 && <Pagination
        value={currentPage}
        onChange={setPage}
        total={count} />}
    </Stack></Card>;
};
export default ExecutionTraceLog;
