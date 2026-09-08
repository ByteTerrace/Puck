import { useEffect, useRef } from "react";
import { Badge, Button, Group, Stack, Text } from "@mantine/core";
import { json } from "@codemirror/lang-json";
import { Decoration, type DecorationSet, EditorView, lineNumbers } from "@codemirror/view";
import { StateEffect, StateField } from "@codemirror/state";
import { EditorView as CmEditorView, minimalSetup } from "codemirror";
import { StudioContext, useStudioDocument } from "../../../context/StudioContext";
import type { EngineDiagnostic } from "../../../native/engineTypes";

/**
 * Best-effort maps one engine diagnostic path (`"state.world[3].cells[0].key"`,
 * `document/jsonPath.ts`'s own `pathToEnginePath` format) to a 1-based line in `text`: walks the
 * path's leading dotted-key segments, searching forward for each one's quoted form in turn — a
 * bracketed array index is skipped (the same integer could appear as any array element's own
 * ordinal, so it carries no reliable textual anchor on its own). Returns the last segment that
 * DID resolve, or `null` if even the first one did not — this is a textual heuristic over a
 * pretty-printed document, not a real JSON-path evaluator; a value that happens to repeat a
 * segment's own key name earlier in the text can point at the wrong occurrence.
 */
export function lineForDiagnosticPath(text: string, path: string): number | null {
  const segments = path.match(/[^.[\]]+/g) ?? [];
  let searchFrom = 0;
  let lastLine: number | null = null;
  for (const segment of segments) {
    if (/^\d+$/.test(segment)) continue;
    const needle = `"${segment}"`;
    const found = text.indexOf(needle, searchFrom);
    if (found < 0) break;
    searchFrom = found + needle.length;
    lastLine = text.slice(0, found).split("\n").length;
  }
  return lastLine;
}

const setDiagnosticLines = StateEffect.define<number[]>();

const diagnosticField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, transaction) {
    let next = decorations.map(transaction.changes);
    for (const effect of transaction.effects) {
      if (effect.is(setDiagnosticLines)) {
        const lineCount = transaction.state.doc.lines;
        const marks = effect.value
          .filter((line) => line >= 1 && line <= lineCount)
          .map((line) => Decoration.line({ class: "puck-json-diagnostic-line" }).range(transaction.state.doc.line(line).from));
        next = Decoration.set(marks, true);
      }
    }
    return next;
  },
  provide: (field) => EditorView.decorations.from(field),
});

const diagnosticTheme = EditorView.baseTheme({
  ".puck-json-diagnostic-line": { backgroundColor: "rgba(220, 90, 90, 0.16)" },
});

/**
 * The studio's JSON tab: a CodeMirror 6 editor over `document.text`. Typing dispatches
 * `SET_TEXT_DRAFT` on every change (the machine keeps this as a live, non-revisioned edit of
 * `context.document.text` — accepted throughout the document region); Apply
 * dispatches `APPLY_TEXT` (parses, intake-checks, becomes a real revision); Discard resets the
 * draft back to the last applied revision's exact text. Diagnostics are shown as a
 * highlighted-line decoration wherever `lineForDiagnosticPath` can map one, plus a plain list
 * underneath for the diagnostics it cannot place.
 */
export function JsonEditor() {
  const actor = StudioContext.useActorRef();
  const document = useStudioDocument();
  const containerRef = useRef<HTMLDivElement>(null);
  const viewRef = useRef<CmEditorView | null>(null);

  useEffect(() => {
    if (!containerRef.current) return;
    const view = new CmEditorView({
      doc: document.text,
      extensions: [
        minimalSetup,
        lineNumbers(),
        EditorView.contentAttributes.of({ "aria-label": "Document JSON" }),
        json(),
        diagnosticField,
        diagnosticTheme,
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            actor.send({ type: "SET_TEXT_DRAFT", text: update.state.doc.toString() });
          }
        }),
      ],
      parent: containerRef.current,
    });
    viewRef.current = view;
    return () => { viewRef.current = null; view.destroy(); };
    // The editor mounts once; `document.text` changes below sync into it imperatively instead of
    // re-mounting (a re-mount on every keystroke would fight the user's own cursor).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [actor]);

  useEffect(() => {
    const view = viewRef.current;
    if (view && view.state.doc.toString() !== document.text) {
      view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: document.text } });
    }
  }, [document.text]);

  useEffect(() => {
    const view = viewRef.current;
    if (!view) return;
    const lines = document.diagnostics
      .map((diagnostic: EngineDiagnostic) => lineForDiagnosticPath(document.text, diagnostic.path))
      .filter((line): line is number => line !== null);
    view.dispatch({ effects: setDiagnosticLines.of(lines) });
  }, [document.diagnostics, document.text]);

  const applied = document.appliedText;
  const isDraft = document.text !== applied;
  const unplaced = document.diagnostics.filter((d) => lineForDiagnosticPath(document.text, d.path) === null);

  return (
    <Stack gap="sm">
      <Group justify="space-between">
        <Group gap="xs">
          <Badge size="xs" variant="light" color={isDraft ? "yellow" : "teal"}>{isDraft ? "unapplied edits" : "applied"}</Badge>
          <Text size="xs" c="dimmed">{document.diagnostics.length} diagnostic{document.diagnostics.length === 1 ? "" : "s"}</Text>
        </Group>
        <Group gap="xs">
          <Button size="compact-xs" variant="default" disabled={!isDraft} onClick={() => actor.send({ type: "SET_TEXT_DRAFT", text: applied })}>Discard</Button>
          <Button size="compact-xs" disabled={!isDraft} onClick={() => actor.send({ type: "APPLY_TEXT", text: document.text })}>Apply</Button>
        </Group>
      </Group>
      <div ref={containerRef} style={{ minHeight: 420, border: "1px solid var(--rule)", borderRadius: 8, overflow: "hidden" }} />
      {document.diagnostics.length > 0 && (
        <Stack gap={4}>
          {document.diagnostics.map((diagnostic, index) => (
            <Text key={index} size="xs" c="red" ff="monospace">
              {diagnostic.path || "$"}: {diagnostic.message}
            </Text>
          ))}
          {unplaced.length > 0 && (
            <Text size="xs" c="dimmed">{unplaced.length} diagnostic{unplaced.length === 1 ? "" : "s"} could not be mapped to a line — the path names an array element or a location not textually anchored.</Text>
          )}
        </Stack>
      )}
    </Stack>
  );
}

export default JsonEditor;
