import { useEffect, useState } from "react";
import { Alert, Button, Group, Loader, Paper, Slider, Stack, Tabs, Text } from "@mantine/core";
import {
  RiBracesLine,
  RiCodeSSlashLine,
  RiErrorWarningLine,
  RiGridLine,
  RiListSettingsLine,
  RiPlayLine,
  RiRestartLine,
  RiSkipBackLine,
  RiSkipForwardLine,
  RiStopLine,
  RiTableLine,
  RiTerminalBoxLine,
} from "@remixicon/react";
import {
  StudioContext,
  useStudioCanStartPreview,
  useStudioCanStepBack,
  useStudioCanStepForward,
  useStudioCompiled,
  useStudioEngineRefusal,
  useStudioIsDirty,
  useStudioPreview,
  useStudioPreviewBusy,
  useStudioWorkspace,
} from "../../context/StudioContext";
import { ruleSpan } from "../../authoring/sourceMap";
import { previewRefusal } from "../../machines/studioMachine";
import { Kicker } from "../../ui/Kicker";
import { CompiledTab, SectionsNavigator } from "./authoring/CompiledViews";
import { SourceEditor } from "./authoring/SourceEditor";
import { DraftsPanel } from "./drafts/DraftsPanel";
import PuckReplConsole from "./PuckReplConsole";
import { RuleTraceView } from "./RuleTraceView";
import StateMatrixView from "./StateMatrixView";
import { StudioConfirmation, useStudioConfirmation } from "./StudioConfirmation";
import classes from "./StudioShell.module.css";
import WorldStudioAlerts from "./WorldStudioAlerts";
import WorldStudioHeader from "./WorldStudioHeader";
import WorldWorkbench from "./WorldWorkbench";

type StudioTab = "source" | "compiled" | "sections" | "spatial" | "state" | "preview" | "console";

/** Exported so a test can render just the Preview tab's own content against a hand-advanced machine snapshot:
 * `Tabs.Panel keepMounted={false}` renders only the active tab under `renderToStaticMarkup`. */
export function PreviewTab() {
  const actor = StudioContext.useActorRef();
  const preview = useStudioPreview();
  const busy = useStudioPreviewBusy();
  const canStart = useStudioCanStartPreview();
  const canStepBack = useStudioCanStepBack();
  const canStepForward = useStudioCanStepForward();
  const workspace = useStudioWorkspace();
  const compiled = useStudioCompiled();
  const engineRefusal = useStudioEngineRefusal();
  const waiting = StudioContext.useSelector((snapshot) => (snapshot.context.preview.status === "idle" ? previewRefusal(snapshot.context) : null));
  const [seek, setSeek] = useState(preview.cursor);
  useEffect(() => setSeek(preview.cursor), [preview.cursor]);
  useEffect(() => {
    // The rule trace links a rule to its source through the open file's compiled source map.
    if (preview.status === "ready") actor.send({ type: "REQUEST_COMPILED" });
  }, [actor, preview.status]);

  const current = preview.snapshots[preview.cursor] ?? null;
  const ready = preview.status === "ready";
  const stopped = preview.status === "idle" || preview.status === "refused";
  const locate = (rule: string) =>
    (compiled && workspace && compiled.workspaceId === workspace.id && compiled.path === workspace.active)
      ? ruleSpan(compiled.value, compiled.sourceMap, rule)
      : null;

  return (
    <Stack gap="md">
      <Paper p="sm">
        <Group gap="sm" justify="space-between" wrap="wrap">
          <Group gap="xs" wrap="wrap">
            {stopped && (
              <Button size="xs" leftSection={<RiPlayLine size={16} />} disabled={!canStart} onClick={() => actor.send({ type: "PREVIEW_START" })}>
                Start preview
              </Button>
            )}
            {ready && (
              <>
                <Button size="xs" leftSection={<RiPlayLine size={16} />} disabled={busy} onClick={() => actor.send({ type: "PREVIEW_TICK" })}>
                  Tick
                </Button>
                <Button size="xs" variant="default" leftSection={<RiSkipBackLine size={16} />} disabled={!canStepBack} onClick={() => actor.send({ type: "PREVIEW_UNDO" })}>
                  Step back
                </Button>
                <Button size="xs" variant="default" leftSection={<RiSkipForwardLine size={16} />} disabled={!canStepForward} onClick={() => actor.send({ type: "PREVIEW_REDO" })}>
                  Step forward
                </Button>
                <Button size="xs" variant="default" leftSection={<RiRestartLine size={16} />} disabled={busy} onClick={() => actor.send({ type: "RESET_WORLD" })}>
                  Reset
                </Button>
              </>
            )}
            {(ready || busy) && (
              <Button size="xs" variant="subtle" color="red" leftSection={<RiStopLine size={16} />} onClick={() => actor.send({ type: "PREVIEW_STOP" })}>
                Stop
              </Button>
            )}
          </Group>
          {busy && (
            <Group gap="xs" wrap="nowrap">
              <Loader size="xs" />
              <Text size="sm" c="dimmed" role="status">Updating preview…</Text>
            </Group>
          )}
        </Group>

        {ready && (
          <Group gap="sm" mt="sm" align="center" wrap="nowrap">
            <Group gap={6} wrap="nowrap" className={classes.tickReadout}>
              <Kicker c="dimmed">Tick</Kicker>
              <Text size="sm" ff="monospace">{current?.tick.toString() ?? "0"}</Text>
            </Group>
            <Slider
              aria-label="Jump to preview tick"
              className={classes.scrubber}
              min={0}
              max={Math.max(0, preview.snapshots.length - 1)}
              value={seek}
              onChange={setSeek}
              onChangeEnd={(index) => actor.send({ type: "JUMP_TO_TICK", index })}
              label={(index) => preview.snapshots[index]?.tick.toString() ?? String(index)}
              disabled={busy || preview.snapshots.length <= 1}
            />
            <Text size="xs" c="dimmed" ff="monospace">
              {preview.cursor + 1}/{preview.snapshots.length}
            </Text>
          </Group>
        )}
      </Paper>

      {/* A refused build or engine shows where a refused start does, before any press: the start is not taken. */}
      {(stopped && (engineRefusal || preview.status === "refused")) && (
        <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Preview refused">
          {engineRefusal ?? preview.refusal ?? "the preview session was refused."}
        </Alert>
      )}

      {ready && (
        <>
          {preview.refusals.length > 0 && (
            <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Write refused">
              {preview.refusals[preview.refusals.length - 1]}
            </Alert>
          )}
          <RuleTraceView
            trace={current?.trace ?? null}
            locate={locate}
            onReveal={(span) => actor.send({ type: "REVEAL_SOURCE", path: span.path, line: span.line, column: span.column, length: span.length })}
          />
        </>
      )}

      {(waiting && !engineRefusal) && <Text size="xs" c="dimmed">{waiting}</Text>}
    </Stack>
  );
}

/**
 * The studio's whole composed UI — header, alerts, the local drafts panel, the tool strip (Source, Compiled,
 * Sections, Spatial, State, Preview, Console), and the unsaved-changes confirmation — bound entirely to an
 * already-constructed `StudioContext`. It carries no `import.meta` of its own, so tests render it directly; the
 * Provider and its input are `WorldStudio.tsx`'s job.
 */
export function StudioShell() {
  const confirm = useStudioConfirmation();
  const isDirty = useStudioIsDirty();
  const workspace = useStudioWorkspace();
  const [activeTab, setActiveTab] = useState<StudioTab>("source");

  // A reveal (from a diagnostic, the compiled views, or the rule trace) shows the source it names.
  const revealNonce = workspace?.reveal?.nonce ?? 0;
  useEffect(() => {
    if (revealNonce > 0) setActiveTab("source");
  }, [revealNonce]);

  useEffect(() => {
    const guard = (event: BeforeUnloadEvent) => { if (isDirty) { event.preventDefault(); event.returnValue = ""; } };
    const navigateGuard = (event: Event) => {
      if (!isDirty) return;
      event.preventDefault();
      void confirm("Discard unsaved source edits and leave the studio?").then((accepted) => {
        if (accepted) (event as CustomEvent).detail?.continueNavigation();
      });
    };
    window.addEventListener("beforeunload", guard);
    window.addEventListener("puck-before-navigate", navigateGuard);
    return () => {
      window.removeEventListener("beforeunload", guard);
      window.removeEventListener("puck-before-navigate", navigateGuard);
    };
  }, [isDirty, confirm]);

  return (
    <Stack gap="md" className={classes.studio}>
      <WorldStudioHeader />
      <WorldStudioAlerts />
      {/* Above the tools, not below them: the panels differ in height, and nothing may move when a tab changes. */}
      <DraftsPanel />

      {/* The Source panel stays mounted while hidden: its editor holds the language server's open file, so diagnostics,
          the island check, and the preview keep following the source whichever tab is shown. */}
      <Tabs value={activeTab} onChange={(value) => setActiveTab((value as StudioTab) ?? "source")} keepMounted={false} keepMountedMode="display-none">
        <Tabs.List>
          <Tabs.Tab value="source" leftSection={<RiCodeSSlashLine size={16} />}>Source</Tabs.Tab>
          <Tabs.Tab value="compiled" leftSection={<RiBracesLine size={16} />}>Compiled</Tabs.Tab>
          <Tabs.Tab value="sections" leftSection={<RiListSettingsLine size={16} />}>Sections</Tabs.Tab>
          <Tabs.Tab value="spatial" leftSection={<RiGridLine size={16} />}>Spatial</Tabs.Tab>
          <Tabs.Tab value="state" leftSection={<RiTableLine size={16} />}>State</Tabs.Tab>
          <Tabs.Tab value="preview" leftSection={<RiPlayLine size={16} />}>Preview</Tabs.Tab>
          <Tabs.Tab value="console" leftSection={<RiTerminalBoxLine size={16} />}>Console</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="source" pt="md" keepMounted><SourceEditor /></Tabs.Panel>
        <Tabs.Panel value="compiled" pt="md"><CompiledTab /></Tabs.Panel>
        <Tabs.Panel value="sections" pt="md"><SectionsNavigator /></Tabs.Panel>
        <Tabs.Panel value="spatial" pt="md"><WorldWorkbench /></Tabs.Panel>
        <Tabs.Panel value="state" pt="md"><StateMatrixView /></Tabs.Panel>
        <Tabs.Panel value="preview" pt="md"><PreviewTab /></Tabs.Panel>
        <Tabs.Panel value="console" pt="md"><PuckReplConsole /></Tabs.Panel>
      </Tabs>
    </Stack>
  );
}

export function StudioShellWithConfirmation() {
  return (
    <StudioConfirmation>
      <StudioShell />
    </StudioConfirmation>
  );
}

export default StudioShell;
