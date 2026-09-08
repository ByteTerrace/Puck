import type { KeyboardEvent } from "react";
import { useRef, useState } from "react";
import { ActionIcon, Badge, Card, Group, Paper, ScrollArea, SegmentedControl, Stack, Text, TextInput, Tooltip } from "@mantine/core";
import { RiDeleteBinLine, RiSendPlaneLine, RiTerminalBoxLine } from "@remixicon/react";
import { useStudioEngine, useStudioPreview } from "../../context/StudioContext";

interface ReplEntry {
  readonly input: string;
  readonly kind: "Int" | "Fixed";
  readonly output: string;
  readonly isError: boolean;
}

/**
 * `engine.evaluate(handle, expression, kind, tick)` over the studio's live preview handle — the
 * one piece of the old REPL (`engine/evaluator.ts`'s JS reimplementation of Puck expression
 * syntax) that survives, now run by the real engine instead of a parallel evaluator. Refuses by
 * name (no `TextInput` even enabled) when no preview session is running: an expression can only
 * mean something against a compiled handle's own live state.
 */
export function PuckReplConsole() {
  const engine = useStudioEngine();
  const preview = useStudioPreview();
  const [kind, setKind] = useState<"Int" | "Fixed">("Int");
  const [input, setInput] = useState("");
  const [history, setHistory] = useState<ReplEntry[]>([]);
  const [historyIndex, setHistoryIndex] = useState(-1);
  const scrollRef = useRef<HTMLDivElement>(null);

  const handle = preview.status === "ready" ? preview.handle : null;

  const evaluate = () => {
    const trimmed = input.trim();
    if (!trimmed || !engine || !handle) return;
    void engine.evaluate(handle, trimmed, kind, preview.tick).then((result) => {
      const entry: ReplEntry = result.ok
        ? { input: trimmed, kind, output: (result.value ?? 0n).toString(), isError: false }
        : { input: trimmed, kind, output: result.error ?? "evaluation refused.", isError: true };
      setHistory((prev) => [...prev, entry]);
      requestAnimationFrame(() => scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" }));
    });
    setInput("");
    setHistoryIndex(-1);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Enter") {
      event.preventDefault();
      evaluate();
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      if (history.length === 0) return;
      const next = historyIndex === -1 ? history.length - 1 : Math.max(0, historyIndex - 1);
      setHistoryIndex(next);
      setInput(history[next].input);
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      if (historyIndex === -1) return;
      const next = historyIndex + 1;
      if (next >= history.length) { setHistoryIndex(-1); setInput(""); }
      else { setHistoryIndex(next); setInput(history[next].input); }
    }
  };

  return (
    <Card withBorder radius="md" p="sm">
      <Group justify="space-between" mb="xs">
        <Group gap="xs">
          <RiTerminalBoxLine size={18} />
          <Text fw={600} size="sm">Puck expression console</Text>
          <SegmentedControl size="xs" value={kind} onChange={(value) => setKind(value as "Int" | "Fixed")} data={["Int", "Fixed"]} />
        </Group>
        <Tooltip label="Clear history">
          <ActionIcon size="sm" variant="subtle" color="gray" onClick={() => setHistory([])} aria-label="Clear console history">
            <RiDeleteBinLine size={14} />
          </ActionIcon>
        </Tooltip>
      </Group>

      {!handle
        ? <Text size="sm" c="dimmed" role="status">No preview session — start one from the Preview tab before evaluating expressions.</Text>
        : (
          <>
            <Paper p="xs" radius="sm" withBorder style={{ minHeight: 160, maxHeight: 260 }}>
              <ScrollArea h={200} viewportRef={scrollRef} offsetScrollbars>
                <Stack gap={6}>
                  {history.map((entry, index) => (
                    <div key={index}>
                      <Group gap={6} wrap="nowrap"><Text size="xs" ff="monospace" fw={700}>puck&gt;</Text><Text size="xs" ff="monospace" style={{ wordBreak: "break-all" }}>{entry.input}</Text><Badge size="xs" variant="outline">{entry.kind}</Badge></Group>
                      <Group gap={6} wrap="nowrap" ml={18}><Text size="xs" ff="monospace" c={entry.isError ? "red" : "dimmed"}>{"⇒"}</Text><Text size="xs" ff="monospace" c={entry.isError ? "red" : undefined} style={{ wordBreak: "break-all" }}>{entry.output}</Text></Group>
                    </div>
                  ))}
                  {history.length === 0 && <Text size="xs" c="dimmed">Evaluate an expression against tick {preview.tick.toString()}.</Text>}
                </Stack>
              </ScrollArea>
            </Paper>
            <Group gap="xs" mt="xs">
              <TextInput
                flex={1}
                size="xs"
                placeholder="Evaluate a Puck expression"
                value={input}
                onChange={(event) => setInput(event.currentTarget.value)}
                onKeyDown={onKeyDown}
                styles={{ input: { fontFamily: "ui-monospace,monospace" } }}
              />
              <ActionIcon size="sm" color="coral" variant="filled" onClick={evaluate} disabled={!input.trim()} aria-label="Evaluate">
                <RiSendPlaneLine size={14} />
              </ActionIcon>
            </Group>
          </>
        )}
    </Card>
  );
}

export default PuckReplConsole;
