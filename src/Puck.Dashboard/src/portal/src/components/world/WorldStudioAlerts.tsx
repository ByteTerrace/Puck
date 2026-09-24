import { useId, useState } from "react";
import { Alert, Badge, Button, Code, Collapse, Group, ScrollArea, Stack, Text, ThemeIcon } from "@mantine/core";
import { RiCheckboxCircleLine, RiErrorWarningLine, RiInformationLine } from "@remixicon/react";
import { StudioContext, useStudioChecking, useStudioProblems, useStudioRefusals, useStudioWorkspace } from "../../context/StudioContext";
import type { SourceSeverity } from "../../native/engineTypes";
import classes from "./WorldStudioAlerts.module.css";

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

const SEVERITY_COLOR: Record<SourceSeverity, string> = { error: "red", warning: "yellow", information: "gray" };

/**
 * The workspace's problems: every current source diagnostic, the island check's findings for the current revision,
 * and what the studio itself refused (an open, a save). A one-line summary always holds the same height, and the
 * list opens only when the author asks, so a diagnostic arriving never moves the tabs beneath it. Each problem's
 * "Reveal" shows its span in the Source tab.
 */
export function WorldStudioAlerts() {
  const actor = StudioContext.useActorRef();
  const workspace = useStudioWorkspace();
  const problems = useStudioProblems();
  const refusals = useStudioRefusals();
  const checking = useStudioChecking();
  const [expanded, setExpanded] = useState(false);
  const panelId = useId();
  const errors = problems.filter((problem) => problem.severity === "error").length;
  const others = problems.length - errors;
  const hasDetails = problems.length > 0 || refusals.length > 0;
  const refused = errors > 0 || refusals.length > 0;
  const summary = hasDetails
    ? [
      errors > 0 ? plural(errors, "error") : null,
      others > 0 ? plural(others, "note") : null,
      refusals.length > 0 ? plural(refusals.length, "studio refusal") : null,
    ].filter(Boolean).join(" · ")
    : !workspace ? "No document open." : checking ? "Checking the source…" : "No problems.";
  const clean = !!workspace && !checking && !hasDetails;
  const icon = refused ? <RiErrorWarningLine size={16} /> : clean ? <RiCheckboxCircleLine size={16} /> : <RiInformationLine size={16} />;

  return (
    <div>
      <Group gap={6} wrap="nowrap" role="status" aria-live="polite" className={classes.summary}>
        <ThemeIcon variant="transparent" color={refused ? "red" : clean ? "jade" : "gray"} size="sm">
          {icon}
        </ThemeIcon>
        <Text size="sm" c={refused ? "red" : "dimmed"} fw={refused ? 600 : undefined} truncate="end">
          {summary}
        </Text>
        {hasDetails && (
          <Button
            size="compact-xs"
            variant="subtle"
            color={refused ? "red" : "gray"}
            aria-expanded={expanded}
            aria-controls={panelId}
            onClick={() => setExpanded(!expanded)}
          >
            {expanded ? "Hide" : "Show"}
          </Button>
        )}
      </Group>
      {hasDetails && (
        <Collapse expanded={expanded} keepMountedMode="display-none" id={panelId}>
          <Alert color={refused ? "red" : "gray"} mt="xs">
            <ScrollArea.Autosize mah={220} offsetScrollbars>
              <Stack gap={6}>
                {problems.map((problem, index) => (
                  <div key={index} className={classes.row}>
                    <Badge color={SEVERITY_COLOR[problem.severity]} size="xs">{problem.code || problem.severity}</Badge>
                    <Text size="sm" className={classes.message}>
                      <Code>{problem.line === 0 ? `${problem.path} (whole document)` : `${problem.path}:${problem.line}:${problem.column}`}</Code>{" "}
                      {problem.message}
                    </Text>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      aria-label={`Reveal ${problem.path} line ${problem.line}`}
                      onClick={() => actor.send({ type: "REVEAL_SOURCE", path: problem.path, line: problem.line, column: problem.column, length: problem.length })}
                    >
                      Reveal
                    </Button>
                  </div>
                ))}
                {refusals.map((refusal, index) => (
                  <div key={`refusal-${index}`} className={classes.row}>
                    <Badge color="red" size="xs">studio</Badge>
                    <Text size="sm" className={classes.message}>{refusal}</Text>
                    <span />
                  </div>
                ))}
              </Stack>
            </ScrollArea.Autosize>
          </Alert>
        </Collapse>
      )}
    </div>
  );
}

export default WorldStudioAlerts;
