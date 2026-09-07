import { useEffect, useState } from "react";
import { Box, Button, Group, Slider, Tabs, Text } from "@mantine/core";
import {
  RiCodeSSlashLine,
  RiGridLine,
  RiListSettingsLine,
  RiPlayLine,
  RiTerminalBoxLine,
} from "@remixicon/react";
import { StudioContext, useStudioDocument, useStudioIsDirty, useStudioPreview } from "../../context/StudioContext";
import type { JsonPath } from "../../document/jsonPath";
import { AuthoringWorkspace } from "./authoring/AuthoringWorkspace";
import { JsonEditor } from "./authoring/JsonEditor";
import { DraftsPanel } from "./drafts/DraftsPanel";
import PuckReplConsole from "./PuckReplConsole";
import { RuleTraceView } from "./RuleTraceView";
import { StudioConfirmation, useStudioConfirmation } from "./StudioConfirmation";
import WorldStudioAlerts from "./WorldStudioAlerts";
import WorldStudioHeader from "./WorldStudioHeader";
import WorldWorkbench from "./WorldWorkbench";
import StateMatrixView from "./StateMatrixView";

type StudioTab = "sections" | "json" | "spatial" | "state" | "preview" | "console";

/** Exported so a test can render just the Preview tab's own content against a hand-advanced
 * machine snapshot — `Tabs.Panel keepMounted={false}` means only the ACTIVE tab renders at all
 * under `renderToStaticMarkup` (there is no interactivity to click a tab into view), so this is
 * the one way to exercise a non-default tab's own markup directly, the same way `WorldWorkbench`/
 * `StateMatrixView` are rendered directly rather than through a click path. */
export function PreviewTab() {
  const actor = StudioContext.useActorRef();
  const preview = useStudioPreview();
  const document = useStudioDocument();

  const current = preview.snapshots[preview.cursor] ?? null;

  return (
    <Box>
      <Group gap="xs" mb="md" wrap="wrap">
        {(preview.status === "idle" || preview.status === "refused") && (
          <Button size="xs" onClick={() => actor.send({ type: "PREVIEW_START" })}>Start preview</Button>
        )}
        {preview.status === "compiling" && <Text size="sm" c="dimmed" role="status">Compiling preview…</Text>}
        {preview.status === "ready" && (
          <>
            <Button size="xs" variant="default" onClick={() => actor.send({ type: "PREVIEW_TICK" })}>Tick</Button>
            <Button size="xs" variant="default" disabled={preview.cursor <= 0} onClick={() => actor.send({ type: "PREVIEW_UNDO" })}>Step back</Button>
            <Button size="xs" variant="default" disabled={preview.cursor >= preview.snapshots.length - 1} onClick={() => actor.send({ type: "PREVIEW_REDO" })}>Step forward</Button>
            <Button size="xs" variant="default" onClick={() => actor.send({ type: "RESET_WORLD" })}>Reset</Button>
            <Button size="xs" variant="subtle" color="red" onClick={() => actor.send({ type: "PREVIEW_STOP" })}>Stop</Button>
          </>
        )}
      </Group>

      {preview.status === "refused" && (
        <Text size="sm" c="red" role="alert" mb="md">
          {preview.refusal ?? "the preview session was refused."}
        </Text>
      )}

      {preview.status === "ready" && (
        <>
          <Group gap="sm" mb="md" align="center">
            <Text size="xs" c="dimmed" style={{ minWidth: 90 }}>Tick {current?.tick.toString() ?? "0"}</Text>
            <Slider
              aria-label="Jump to preview tick"
              style={{ flex: 1, maxWidth: 360 }}
              min={0}
              max={Math.max(0, preview.snapshots.length - 1)}
              value={preview.cursor}
              onChange={(index) => actor.send({ type: "JUMP_TO_TICK", index })}
              label={(index) => preview.snapshots[index]?.tick.toString() ?? String(index)}
              disabled={preview.snapshots.length <= 1}
            />
          </Group>
          {preview.refusals.length > 0 && (
            <Text size="xs" c="red" role="alert" mb="sm">{preview.refusals[preview.refusals.length - 1]}</Text>
          )}
          <RuleTraceView trace={current?.trace ?? null} />
        </>
      )}

      {document.validation !== "clean" && preview.status === "idle" && (
        <Text size="xs" c="dimmed" mt="sm">The document has unresolved diagnostics — resolve them before starting a preview.</Text>
      )}
    </Box>
  );
}

/**
 * The studio's whole composed UI — header, alerts, the six-tab tool strip (Sections/JSON/
 * Spatial/State/Preview/Console), the local drafts panel, and the unsaved-changes confirmation —
 * bound entirely to an ALREADY-CONSTRUCTED `StudioContext` (no `import.meta` of its own; the
 * Provider and its `official`/`bootEngine` input are `WorldStudio.tsx`'s job, kept in a separate
 * file for exactly the reason `officialBase.ts` documents: a literal `import.meta` token anywhere
 * in a module breaks this repository's Node/CommonJS test harness the moment anything requires
 * it, so the one file that reads `import.meta.env` stays out of every test's require graph while
 * this one — the actual composed UI a test needs to render — never touches it).
 */
export function StudioShell() {
  const confirm = useStudioConfirmation();
  const isDirty = useStudioIsDirty();
  const [activeTab, setActiveTab] = useState<StudioTab>("sections");
  const [focusSection, setFocusSection] = useState<JsonPath | null>(null);

  useEffect(() => {
    const guard = (event: BeforeUnloadEvent) => { if (isDirty) { event.preventDefault(); event.returnValue = ""; } };
    const navigateGuard = (event: Event) => {
      if (!isDirty) return;
      event.preventDefault();
      void confirm("Discard unsaved document edits and leave the studio?").then((accepted) => {
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

  const onReveal = (path: JsonPath) => {
    setFocusSection(path);
    setActiveTab("sections");
  };

  return (
    <Box p="md" className="world-studio">
      <WorldStudioHeader />
      <WorldStudioAlerts onReveal={onReveal} />

      <Tabs value={activeTab} onChange={(value) => setActiveTab((value as StudioTab) ?? "sections")} variant="outline" keepMounted={false} mt="md">
        <Tabs.List style={{ flexWrap: "wrap" }}>
          <Tabs.Tab value="sections" leftSection={<RiListSettingsLine size={15} />}>Sections</Tabs.Tab>
          <Tabs.Tab value="json" leftSection={<RiCodeSSlashLine size={15} />}>JSON</Tabs.Tab>
          <Tabs.Tab value="spatial" leftSection={<RiGridLine size={15} />}>Spatial</Tabs.Tab>
          <Tabs.Tab value="state" leftSection={<RiListSettingsLine size={15} />}>State</Tabs.Tab>
          <Tabs.Tab value="preview" leftSection={<RiPlayLine size={15} />}>Preview</Tabs.Tab>
          <Tabs.Tab value="console" leftSection={<RiTerminalBoxLine size={15} />}>Console</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="sections" pt="md"><AuthoringWorkspace focusSection={focusSection} /></Tabs.Panel>
        <Tabs.Panel value="json" pt="md"><JsonEditor /></Tabs.Panel>
        <Tabs.Panel value="spatial" pt="md"><WorldWorkbench /></Tabs.Panel>
        <Tabs.Panel value="state" pt="md"><StateMatrixView /></Tabs.Panel>
        <Tabs.Panel value="preview" pt="md"><PreviewTab /></Tabs.Panel>
        <Tabs.Panel value="console" pt="md"><PuckReplConsole /></Tabs.Panel>
      </Tabs>

      <Box mt="md"><DraftsPanel /></Box>
    </Box>
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
