import {
  autocompletion,
  Completion,
  CompletionContext,
  CompletionResult,
} from "@codemirror/autocomplete";
import { sql } from "@codemirror/lang-sql";
import { lineNumbers } from "@codemirror/view";
import { EditorView, minimalSetup } from "codemirror";
import { useEffect, useRef } from "react";

const KEYWORDS = [
  "SELECT",
  "FROM",
  "WHERE",
  "GROUP BY",
  "ORDER BY",
  "HAVING",
  "LIMIT",
  "JOIN",
  "LEFT JOIN",
  "ON",
  "AS",
  "AND",
  "OR",
  "NOT",
  "IN",
  "BETWEEN",
  "DISTINCT",
  "DESC",
  "ASC",
  "UNION ALL",
  "WITH",
  "CASE",
  "WHEN",
  "THEN",
  "ELSE",
  "END",
];
const FUNCTIONS = [
  "read_parquet",
  "read_csv_auto",
  "read_json_auto",
  "count",
  "avg",
  "sum",
  "min",
  "max",
  "coalesce",
  "round",
  "cast",
  "lower",
  "upper",
  "strftime",
];

export interface SqlFileCompletion {
  label: string;
  url: string;
}

export default function SqlEditor({
  files,
  onChange,
  value,
}: {
  files: SqlFileCompletion[];
  onChange: (value: string) => void;
  value: string;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const filesRef = useRef(files);
  const onChangeRef = useRef(onChange);
  const viewRef = useRef<EditorView>(null);

  filesRef.current = files;
  onChangeRef.current = onChange;

  useEffect(() => {
    if (!containerRef.current) {
      return;
    }

    const completionSource = (
      context: CompletionContext,
    ): CompletionResult | null => {
      const word = context.matchBefore(/[\w./'-]*/);

      if (!word || (word.from === word.to && !context.explicit)) {
        return null;
      }

      const options: Completion[] = [
        ...KEYWORDS.map((keyword) => ({ label: keyword, type: "keyword" })),
        ...FUNCTIONS.map((name) => ({
          apply: `${name}(`,
          label: name,
          type: "function",
        })),
        ...filesRef.current.map((file) => ({
          apply: `'${file.url}'`,
          detail: "your file",
          label: file.label,
          type: "text",
        })),
      ];

      return { from: word.from, options };
    };
    const view = new EditorView({
      doc: value,
      extensions: [
        minimalSetup,
        lineNumbers(),
        sql(),
        autocompletion({ override: [completionSource] }),
        EditorView.updateListener.of((update) => {
          if (update.docChanged) {
            onChangeRef.current(update.state.doc.toString());
          }
        }),
        EditorView.theme({
          "&": {
            backgroundColor: "var(--mantine-color-body, #ffffff)",
            border: "1px solid var(--mantine-color-default-border, #ced4da)",
            borderRadius: "4px",
            fontSize: "13px",
          },
          "&.cm-focused": {
            borderColor: "var(--mantine-primary-color-filled, #228be6)",
            outline: "none",
          },
          ".cm-content": {
            fontFamily: "ui-monospace, Consolas, monospace",
            minHeight: "96px",
          },
          ".cm-gutters": {
            backgroundColor: "transparent",
            border: "none",
            color: "var(--mantine-color-dimmed, #adb5bd)",
          },
        }),
      ],
      parent: containerRef.current,
    });

    viewRef.current = view;

    return () => {
      viewRef.current = null;
      view.destroy();
    };
    // The editor is created once; value sync happens below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => {
    const view = viewRef.current;

    if (view && view.state.doc.toString() !== value) {
      view.dispatch({
        changes: { from: 0, insert: value, to: view.state.doc.length },
      });
    }
  }, [value]);

  return <div ref={containerRef} />;
}
