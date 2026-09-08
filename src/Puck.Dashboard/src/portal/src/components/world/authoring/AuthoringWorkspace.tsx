import { useEffect, useMemo, useState } from "react";
import { Stack, Text } from "@mantine/core";
import { StudioContext, useStudioDocument, useStudioOfficial } from "../../../context/StudioContext";
import type { JsonPath } from "../../../document/jsonPath";
import { ExtensionsEditor, resolve, SectionExplorer, SectionForm, type DocumentEdit, type JsonSchema } from "../../../forms";

/**
 * The studio's Sections tab: a form over the schema bundle instead of raw JSON —
 * `SectionExplorer` lists every root section (schema order), `SectionForm` renders the selected
 * one, and the root `extensions` bag (every reserved-`$`/`_`-prefixed top-level key the schema
 * does not otherwise name) sits below as its own always-present entry. Bound to `StudioContext`
 * for the document and the edit dispatch; `focusSection` (from `WorldStudio`'s alert "reveal")
 * only ever selects a root section — `SectionForm` exposes no deeper per-field focus of its own
 * (see this package's own CONTRACT notes), so a diagnostic nested under an authored section still
 * only gets you to that section, not the exact field.
 */
export function AuthoringWorkspace({ focusSection }: { readonly focusSection?: JsonPath | null }) {
  const actor = StudioContext.useActorRef();
  const document = useStudioDocument();
  const official = useStudioOfficial();

  const walker = useMemo(() => (official ? resolve(official.schemaBundle as JsonSchema) : null), [official]);
  const sections = walker?.rootSections() ?? [];

  const [selected, setSelected] = useState<JsonPath | "extensions">(sections[0]?.path ?? []);
  useEffect(() => {
    if (sections.length > 0 && selected.length === 0 && selected !== "extensions") {
      setSelected(sections[0].path);
    }
    // Only seeds an initial selection; never overrides a later user pick.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sections.length]);
  useEffect(() => {
    if (focusSection && focusSection.length > 0) {
      const match = sections.find((section) => section.key === focusSection[0]);
      if (match) setSelected(match.path);
    }
  }, [focusSection, sections]);

  const onEdit = (edit: DocumentEdit) => {
    actor.send({ type: "EDIT_DOCUMENT", path: edit.path, value: edit.value, label: edit.label });
  };

  if (!walker) {
    return <Text size="sm" c="dimmed">No schema bundle available yet — the official build has not booted.</Text>;
  }

  return (
    <div className="studio-authoring" aria-label="Document sections">
      <aside className="studio-explorer" aria-label="Document sections">
        <Text className="kicker" mb="md">Sections</Text>
        <SectionExplorer bundle={official!.schemaBundle as JsonSchema} document={document.value} selected={selected === "extensions" ? undefined : selected} onSelect={setSelected} />
        <button
          type="button"
          className="studio-explorer-item"
          aria-pressed={selected === "extensions"}
          onClick={() => setSelected("extensions")}
          style={{ marginTop: 12, width: "100%" }}
        >
          <span>Extensions</span><small>$ / _ reserved keys</small>
        </button>
      </aside>
      <div className="studio-viewport-column" style={{ gridColumn: "2 / -1" }}>
        <div style={{ padding: "14px 18px" }}>
          <Stack gap="lg">
            {selected === "extensions"
              ? (
                <>
                  <Text component="h2" size="lg" fw={600} m={0}>Extensions</Text>
                  <ExtensionsEditor walker={walker} document={document.value} onEdit={onEdit} />
                </>
              )
              : <SectionForm bundle={official!.schemaBundle as JsonSchema} document={document.value} path={selected} onEdit={onEdit} />}
          </Stack>
        </div>
      </div>
    </div>
  );
}

export default AuthoringWorkspace;
