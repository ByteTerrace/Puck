import type { KeyboardEvent } from "react";
import { useRef, useState } from "react";
import { ActionIcon, Badge, Group, Paper, ScrollArea, SegmentedControl, Stack, Text, TextInput, Title, Tooltip } from "@mantine/core";
import { RiDeleteBinLine, RiSendPlaneLine, RiTerminalBoxLine } from "@remixicon/react";
import { useStudioPreview, useStudioWorldEngine } from "../../context/StudioContext";
import { EmptyState } from "../../ui/EmptyState";
import { Kicker } from "../../ui/Kicker";
import classes from "./PuckReplConsole.module.css";
import { Well } from "../../ui/Well";

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
  const engine = useStudioWorldEngine();
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
    <Paper p="md">
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start" gap="sm">
          <Stack gap={2}>
            <Kicker>{handle ? `Live preview · tick ${preview.tick.toString()}` : "Console"}</Kicker>
            <Title order={2} size="h4">Puck expression console</Title>
          </Stack>
          <Group gap="xs" wrap="nowrap">
            <SegmentedControl
              aria-label="Evaluate as"
              size="xs"
              value={kind}
              onChange={(value) => setKind(value as "Int" | "Fixed")}
              data={["Int", "Fixed"]}
            />
            <Tooltip label="Clear history">
              <ActionIcon size="input-xs" color="gray" onClick={() => setHistory([])} disabled={history.length === 0} aria-label="Clear console history">
                <RiDeleteBinLine size={16} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>

        {!handle
          ? (
            <div role="status">
              <EmptyState icon={<RiTerminalBoxLine size={22} />} title="No preview session">
                Start one from the Preview tab before evaluating expressions.
              </EmptyState>
            </div>
          )
          : (
            <>
              <Well ff="monospace">
                <ScrollArea h={220} viewportRef={scrollRef} offsetScrollbars>
                  {history.length === 0
                    ? <Text size="xs" c="dimmed" p="sm">Evaluate an expression against tick {preview.tick.toString()}.</Text>
                    : (
                      <ol className={classes.log} aria-label="Console history">
                        {history.map((entry, index) => (
                          <li key={index} className={classes.entry}>
                            <div className={classes.line}>
                              <Text size="xs" ff="monospace" className={classes.prompt}>puck&gt;</Text>
                              <Text size="xs" ff="monospace" className={classes.value}>{entry.input}</Text>
                              <Badge size="xs" color="gray">{entry.kind}</Badge>
                            </div>
                            <div className={classes.line}>
                              <Text size="xs" ff="monospace" c={entry.isError ? "red" : "dimmed"} aria-hidden>⇒</Text>
                              <Text size="xs" ff="monospace" c={entry.isError ? "red" : undefined} className={classes.value}>{entry.output}</Text>
                            </div>
                          </li>
                        ))}
                      </ol>
                    )}
                </ScrollArea>
              </Well>
              <Group gap="xs" wrap="nowrap">
                <TextInput
                  flex={1}
                  size="xs"
                  aria-label="Puck expression"
                  placeholder="Evaluate a Puck expression"
                  leftSection={<Text size="xs" ff="monospace" className={classes.prompt} aria-hidden>&gt;</Text>}
                  value={input}
                  onChange={(event) => setInput(event.currentTarget.value)}
                  onKeyDown={onKeyDown}
                  classNames={{ input: classes.input }}
                />
                <ActionIcon size="input-xs" variant="filled" onClick={evaluate} disabled={!input.trim()} aria-label="Evaluate">
                  <RiSendPlaneLine size={16} />
                </ActionIcon>
              </Group>
            </>
          )}
      </Stack>
    </Paper>
  );
}

export default PuckReplConsole;
