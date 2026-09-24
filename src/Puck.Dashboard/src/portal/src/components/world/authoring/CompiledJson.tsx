import { useEffect, useRef, useState } from "react";
import { Button, Code, Text } from "@mantine/core";
import { RiCodeSSlashLine } from "@remixicon/react";
import { json } from "@codemirror/lang-json";
import { EditorState } from "@codemirror/state";
import { EditorView, lineNumbers } from "@codemirror/view";
import { minimalSetup } from "codemirror";
import { spanAt } from "../../../authoring/sourceMap";
import { serializeDocumentText } from "../../../document/jsonText";
import { pathToPointer, pointerToPath, type JsonPath } from "../../../document/jsonPath";
import type { SourceMap, SourceSpan } from "../../../native/engineTypes";
import { puckEditorTheme } from "../../../ui/codemirror";
import { jsonPointerAt } from "./jsonPointerAt";
import classes from "./CompiledJson.module.css";

/**
 * A read-only view of compiled IR: `value` as JSON text, the pointer of the value under the cursor, and the source
 * span the source map resolves it to. `basePath` places `value` inside the whole compiled document, so a section
 * shown alone still resolves against the document's own map. The status line keeps its height whatever it says.
 */
export function CompiledJson({ value, basePath, sourceMap, onReveal, label }: {
  readonly value: unknown;
  readonly basePath: JsonPath;
  readonly sourceMap: SourceMap;
  readonly onReveal: (span: SourceSpan) => void;
  readonly label: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [pointer, setPointer] = useState(pathToPointer(basePath));
  const base = pathToPointer(basePath);
  const text = value === undefined ? "" : serializeDocumentText(value);

  useEffect(() => {
    if (!containerRef.current) return;
    setPointer(base);
    const view = new EditorView({
      parent: containerRef.current,
      state: EditorState.create({
        doc: text,
        extensions: [
          minimalSetup,
          lineNumbers(),
          json(),
          puckEditorTheme,
          EditorState.readOnly.of(true),
          EditorView.contentAttributes.of({ "aria-label": label }),
          EditorView.updateListener.of((update) => {
            if (!update.selectionSet) return;
            const inner = jsonPointerAt(update.state, update.state.selection.main.head);
            setPointer(pathToPointer([...basePath, ...pointerToPath(inner)]));
          }),
        ],
      }),
    });
    return () => view.destroy();
    // The view is rebuilt only when what it shows changes.
  }, [text, base]);

  const span = spanAt(sourceMap, pointer);

  return (
    <div className={classes.frame}>
      <div className={classes.status} role="status">
        <Code className={classes.pointer} title={pointer || "/"}>{pointer || "/"}</Code>
        <Text size="xs" c="dimmed" className={classes.span} title={span ? `${span.path}:${span.line}:${span.column}` : undefined}>
          {span ? `${span.path}:${span.line}:${span.column}${span.module ? ` · ${span.module}` : ""}` : "no source span"}
        </Text>
        <Button size="compact-xs" variant="default" leftSection={<RiCodeSSlashLine size={14} />} disabled={!span} onClick={() => span && onReveal(span)}>
          Go to source
        </Button>
      </div>
      <div className={classes.viewer} ref={containerRef} />
    </div>
  );
}

export default CompiledJson;
