import { useEffect, useState } from "react";
import { Badge, Loader, Stack, Text, Title } from "@mantine/core";
import { RiBracesLine, RiFileList3Line } from "@remixicon/react";
import { StudioContext, useStudioCompiled, useStudioOfficial, useStudioWorkspace } from "../../../context/StudioContext";
import { getAt } from "../../../document/jsonPath";
import { isPuckSource } from "../../../document/sourcePaths";
import type { CompiledView } from "../../../machines/studio/types";
import type { SourceSpan } from "../../../native/engineTypes";
import { EmptyState } from "../../../ui/EmptyState";
import { ExplorerItem } from "../../../ui/ExplorerItem";
import { Kicker } from "../../../ui/Kicker";
import { CompiledJson } from "./CompiledJson";
import classes from "./CompiledViews.module.css";

/** One root section of the world document schema, in declaration order. */
export interface RootSection {
  readonly key: string;
  readonly title?: string;
  readonly description?: string;
}

/** The world document's root sections, read from the schema bundle's own `properties`. */
export function rootSections(bundle: unknown): RootSection[] {
  const properties = (bundle as { properties?: Record<string, { title?: unknown; description?: unknown }> } | null)?.properties ?? {};
  return Object.entries(properties).map(([key, schema]) => ({
    key,
    title: typeof schema?.title === "string" ? schema.title : undefined,
    description: typeof schema?.description === "string" ? schema.description : undefined,
  }));
}

/**
 * Asks the machine for the open source's compiled IR when a compiled view is shown, and answers the one that belongs
 * to the open file at its current revision. The machine compiles only when its cached answer is out of date, so
 * showing a view again after no edit costs nothing, and typing never compiles.
 */
function useCompiledSource(): { compiled: CompiledView | null; compiling: boolean } {
  const actor = StudioContext.useActorRef();
  const workspace = useStudioWorkspace();
  const compiled = useStudioCompiled();
  const compiling = StudioContext.useSelector((snapshot) => snapshot.matches({ ready: { workspace: { open: { compiled: "compiling" } } } }));
  useEffect(() => {
    actor.send({ type: "REQUEST_COMPILED" });
  }, [actor, workspace?.id, workspace?.active]);
  const current = (compiled && workspace && compiled.workspaceId === workspace.id && compiled.path === workspace.active) ? compiled : null;
  return { compiled: current, compiling };
}

function useReveal() {
  const actor = StudioContext.useActorRef();
  return (span: SourceSpan) => actor.send({ type: "REVEAL_SOURCE", path: span.path, line: span.line, column: span.column, length: span.length });
}

function CompiledStatus({ compiled, compiling }: { readonly compiled: CompiledView | null; readonly compiling: boolean }) {
  const errors = compiled?.result.diagnostics.filter((diagnostic) => diagnostic.severity === "error").length ?? 0;
  return (
    <div className={classes.status} role="status">
      {compiling && <Loader size="xs" />}
      <Text size="xs" c={compiled && !compiled.result.ok ? "red" : "dimmed"}>
        {compiling ? "Compiling…"
          : !compiled ? "Not compiled yet."
            : compiled.result.ok ? `Compiled ${compiled.path} at revision ${compiled.revision}.`
              : `${compiled.path} did not compile: ${errors} error${errors === 1 ? "" : "s"}.`}
      </Text>
    </div>
  );
}

/** The Compiled tab: the open source's whole compiled document, read-only, with each value's way back to source. */
export function CompiledTab() {
  const workspace = useStudioWorkspace();
  const { compiled, compiling } = useCompiledSource();
  const reveal = useReveal();

  if (!workspace || !isPuckSource(workspace.active)) {
    return (
      <EmptyState icon={<RiBracesLine size={22} />} title="Nothing to compile">
        {workspace ? "The open file has no .puck source; its document is already what the compiler would produce." : "Open a document to see what its source compiles to."}
      </EmptyState>
    );
  }
  return (
    <Stack gap="xs">
      <CompiledStatus compiled={compiled} compiling={compiling} />
      <CompiledJson
        label={`Compiled document of ${workspace.active}`}
        value={compiled?.value ?? undefined}
        basePath={[]}
        sourceMap={compiled?.sourceMap ?? {}}
        onReveal={reveal}
      />
    </Stack>
  );
}

/** The Sections tab: the compiled document's root sections in schema order, each shown read-only with its way back
 * to the source that wrote it. */
export function SectionsNavigator() {
  const official = useStudioOfficial();
  const workspace = useStudioWorkspace();
  const { compiled, compiling } = useCompiledSource();
  const reveal = useReveal();
  const [picked, setPicked] = useState<string | null>(null);
  const sections = official ? rootSections(official.schemaBundle) : [];
  const value = compiled?.value ?? null;
  const authored = sections.filter((section) => getAt(value, [section.key]) !== undefined);
  const selected = sections.find((section) => section.key === picked) ?? authored[0] ?? sections[0] ?? null;

  if (!workspace || !official) {
    return (
      <EmptyState icon={<RiFileList3Line size={22} />} title="No sections yet">
        Open a document to browse its compiled sections.
      </EmptyState>
    );
  }

  return (
    <section aria-label="Document sections" className={classes.workspace}>
      <aside aria-label="Section list" className={classes.explorer}>
        <Kicker c="dimmed" mb="sm" px="xs">Sections</Kicker>
        {sections.map((section) => {
          const present = getAt(value, [section.key]) !== undefined;
          return (
            <ExplorerItem
              key={section.key}
              data-section-key={section.key}
              name={section.key}
              description={section.description}
              selected={section.key === selected?.key}
              onClick={() => setPicked(section.key)}
              aside={<Badge color={present ? "jade" : "gray"} size="xs">{present ? "authored" : "absent"}</Badge>}
            />
          );
        })}
      </aside>
      <div className={classes.content}>
        {selected && (
          <Stack gap={4} mb="sm">
            <Kicker>Section</Kicker>
            <Title order={2} size="h3">{selected.title ?? selected.key}</Title>
            <Text c="dimmed" size="sm" lineClamp={2} className={classes.description}>{selected.description ?? " "}</Text>
          </Stack>
        )}
        <CompiledStatus compiled={compiled} compiling={compiling} />
        <CompiledJson
          label={`Compiled ${selected?.key ?? "document"} section`}
          value={selected ? getAt(value, [selected.key]) : undefined}
          basePath={selected ? [selected.key] : []}
          sourceMap={compiled?.sourceMap ?? {}}
          onReveal={reveal}
        />
      </div>
    </section>
  );
}
